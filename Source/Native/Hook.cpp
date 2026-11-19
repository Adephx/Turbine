#include "UdpSocket.h"
#include "UdpCallback.h"
#include <mstcpip.h>
#include <mswsock.h>
#include <array>
#include <mutex>
#include <unordered_map>

uint32_t virtualAddress = 0;
uint32_t virtualMask = 0;

namespace
{
constexpr uint32_t NativeFrameCapacity = 512;
constexpr uint64_t MaximumDatagram = 65483;
HANDLE pipeHandle = INVALID_HANDLE_VALUE;
std::mutex pipeMutex;
std::recursive_mutex socketMutex;

struct SocketState
{
	int type = 0;
	bool registered = false;
	bool internal = false;
	bool poisoned = false;
	bool nonblocking = false;
	bool connected = false;
	bool accepted = false;
	bool loopbackBound = false;
	uint16_t loopbackPort = 0;
	uint16_t localPort = 0;
	uint32_t peerAddress = 0;
	uint16_t peerPort = 0;
	BYTE bindFlags = 0;
	BYTE multicastTtl = 1;
	bool multicastLoop = true;
	uint32_t multicastInterface = 0;
	std::array<uint32_t, 64> groups{};
	size_t groupCount = 0;
};
std::unordered_map<SOCKET, SocketState> sockets;
std::mutex startupMutex;
LONG startupCount = 0;

auto realSocket = ::socket;
auto realWSAStartup = ::WSAStartup;
auto realWSACleanup = ::WSACleanup;
auto realWSASocketA = ::WSASocketA;
auto realWSASocketW = ::WSASocketW;
auto realBind = ::bind;
auto realClose = ::closesocket;
auto realConnect = ::connect;
auto realWSAConnect = ::WSAConnect;
auto realListen = ::listen;
auto realAccept = ::accept;
auto realWSAAccept = ::WSAAccept;
auto realSend = ::send;
auto realSendTo = ::sendto;
auto realRecvFrom = ::recvfrom;
auto realWSASend = ::WSASend;
auto realWSASendTo = ::WSASendTo;
auto realWSARecvFrom = ::WSARecvFrom;
auto realGetSockName = ::getsockname;
auto realGetPeerName = ::getpeername;
auto realSetSockOpt = ::setsockopt;
auto realIoctlSocket = ::ioctlsocket;
auto realWSAIoctl = ::WSAIoctl;
auto realWSAEventSelect = ::WSAEventSelect;
auto realWSAAsyncSelect = ::WSAAsyncSelect;
struct Frame
{
	std::array<BYTE, NativeFrameCapacity> data{};
	uint32_t size = 0;
	uint32_t cursor = 0;
	template <class T> void Put(T value)
	{
		static_assert(sizeof(T) <= 8);
		memcpy(data.data() + size, &value, sizeof(value));
		size += sizeof(value);
	}
	template <class T> bool Get(T &value)
	{
		if (cursor > size || size - cursor < sizeof(value))
			return false;
		memcpy(&value, data.data() + cursor, sizeof(value));
		cursor += sizeof(value);
		return true;
	}
};

bool TransferPipeData(void *bytes, DWORD length, bool write)
{
	BYTE *cursor = static_cast<BYTE *>(bytes);
	while (length)
	{
		OVERLAPPED operation{};
		operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
		if (!operation.hEvent)
			return false;
		DWORD count = 0;
		BOOL completed = write ? WriteFile(pipeHandle, cursor, length, &count, &operation)
		                       : ReadFile(pipeHandle, cursor, length, &count, &operation);
		if (!completed && GetLastError() == ERROR_IO_PENDING)
		{
			DWORD wait = WaitForSingleObject(operation.hEvent, 30000);
			if (wait != WAIT_OBJECT_0)
			{
				CancelIoEx(pipeHandle, &operation);
				GetOverlappedResult(pipeHandle, &operation, &count, TRUE);
			}
			else
				completed = GetOverlappedResult(pipeHandle, &operation, &count, FALSE);
		}
		CloseHandle(operation.hEvent);
		if (!completed || !count || count > length)
			return false;
		cursor += count;
		length -= count;
	}
	return true;
}

int SendRequest(Frame &request, Frame &reply)
{
	std::lock_guard<std::mutex> lock(pipeMutex);
	if (pipeHandle == INVALID_HANDLE_VALUE)
		return WSAENETDOWN;
	if (!TransferPipeData(&request.size, sizeof(request.size), true) ||
	    !TransferPipeData(request.data.data(), request.size, true) ||
	    !TransferPipeData(&reply.size, sizeof(reply.size), false) || reply.size < sizeof(int32_t) ||
	    reply.size > reply.data.size() || !TransferPipeData(reply.data.data(), reply.size, false))
	{
		CloseHandle(pipeHandle);
		pipeHandle = INVALID_HANDLE_VALUE;
		return WSAENETDOWN;
	}
	int32_t error = 0;
	reply.Get(error);
	return error;
}

void RemoveSocketRegistration(SOCKET socket)
{
	Frame request, reply;
	request.Put<BYTE>(7);
	request.Put<uint64_t>(static_cast<uint64_t>(socket));
	SendRequest(request, reply);
}

bool FindSocketState(SOCKET socket, SocketState &result)
{
	std::lock_guard lock(socketMutex);
	auto found = sockets.find(socket);
	if (found == sockets.end())
		return false;
	result = found->second;
	return true;
}

int TrackSocket(SOCKET &socket, int family, int type, bool overlapped = true)
{
	if (socket == INVALID_SOCKET || family != AF_INET)
		return 0;
	SOCKET backing = socket;
	try
	{
		std::lock_guard lock(socketMutex);
		// Winsock forwards socket and WSASocketA through WSASocketW on Windows.
		if (sockets.find(socket) != sockets.end())
			return 0;
		if (type == SOCK_DGRAM && overlapped)
		{
			socket = MakeUdpSocket(backing);
			if (socket == INVALID_SOCKET)
			{
				realClose(backing);
				return WSAENOBUFS;
			}
		}
		SocketState state{};
		state.type = type;
		sockets.emplace(socket, state);
		return 0;
	}
	catch (...)
	{
		ReleaseUdpSocket(socket);
		realClose(backing);
		return WSAENOBUFS;
	}
}

bool IsLoopbackAddress(uint32_t address)
{
	return (address & 0xff000000u) == 0x7f000000u;
}
bool IsMulticastAddress(uint32_t address)
{
	return (address & 0xf0000000u) == 0xe0000000u;
}

int ValidateSocket(int family, int type, int protocol)
{
	if (family == AF_INET6)
		return WSAEAFNOSUPPORT;
	if (family != AF_INET)
		return 0;
	if (type != SOCK_DGRAM && type != SOCK_STREAM)
		return WSAESOCKTNOSUPPORT;
	if (protocol && protocol != (type == SOCK_DGRAM ? IPPROTO_UDP : IPPROTO_TCP))
		return WSAEPROTONOSUPPORT;
	return 0;
}

int WSAAPI HookWSAStartup(WORD version, LPWSADATA data)
{
	std::lock_guard<std::mutex> lock(startupMutex);
	int result = realWSAStartup(version, data);
	if (!result)
		InterlockedIncrement(&startupCount);
	return result;
}

int WSAAPI HookWSACleanup()
{
	std::lock_guard<std::mutex> startupLock(startupMutex);
	bool final = startupCount == 1;
	if (final)
		CancelAllUdpSockets();
	int result = realWSACleanup();
	if (result)
	{
		int error = WSAGetLastError();
		if (final)
			ResumeAllUdpSockets();
		WSASetLastError(error);
		return result;
	}
	if (InterlockedDecrement(&startupCount) > 0)
		return result;
	InterlockedExchange(&startupCount, 0);
	int error = WSAGetLastError();
	{
		std::lock_guard lock(socketMutex);
		for (const auto &item : sockets)
			if (item.second.registered)
				RemoveSocketRegistration(item.first);
		sockets.clear();
	}
	ClearUdpAsyncSelect();
	DiscardUdpCallbacks();
	ReleaseAllUdpSockets();
	WSASetLastError(error);
	return result;
}

SOCKET WSAAPI HookSocket(int family, int type, int protocol)
{
	int error = ValidateSocket(family, type, protocol);
	if (error)
	{
		SocketError(error);
		return INVALID_SOCKET;
	}
	SOCKET result = realSocket(family, type, protocol);
	int openType = 0, optionSize = sizeof(openType);
	if (result != INVALID_SOCKET && family == AF_INET && type == SOCK_DGRAM)
		getsockopt(INVALID_SOCKET, SOL_SOCKET, SO_OPENTYPE, reinterpret_cast<char *>(&openType), &optionSize);
	error = TrackSocket(result, family, type, openType == 0);
	if (error)
	{
		SocketError(error);
		return INVALID_SOCKET;
	}
	return result;
}

SOCKET WSAAPI HookWSASocketA(int family, int type, int protocol, LPWSAPROTOCOL_INFOA info, GROUP group, DWORD flags)
{
	if (info)
	{
		SocketError(WSAEOPNOTSUPP);
		return INVALID_SOCKET;
	}
	int error = ValidateSocket(family, type, protocol);
	if (error)
	{
		SocketError(error);
		return INVALID_SOCKET;
	}
	SOCKET result = realWSASocketA(family, type, protocol, info, group, flags);
	error = TrackSocket(result, family, type, (flags & WSA_FLAG_OVERLAPPED) != 0);
	if (error)
	{
		SocketError(error);
		return INVALID_SOCKET;
	}
	return result;
}

SOCKET WSAAPI HookWSASocketW(int family, int type, int protocol, LPWSAPROTOCOL_INFOW info, GROUP group, DWORD flags)
{
	if (info)
	{
		SocketError(WSAEOPNOTSUPP);
		return INVALID_SOCKET;
	}
	int error = ValidateSocket(family, type, protocol);
	if (error)
	{
		SocketError(error);
		return INVALID_SOCKET;
	}
	SOCKET result = realWSASocketW(family, type, protocol, info, group, flags);
	error = TrackSocket(result, family, type, (flags & WSA_FLAG_OVERLAPPED) != 0);
	if (error)
	{
		SocketError(error);
		return INVALID_SOCKET;
	}
	return result;
}

int RegisterPort(SOCKET socket, const SocketState &state, uint16_t requested, uint16_t loopback, uint16_t &assigned)
{
	Frame request, reply;
	request.Put<BYTE>(2);
	request.Put<uint64_t>(static_cast<uint64_t>(socket));
	request.Put<BYTE>(state.type == SOCK_STREAM ? 1 : 2);
	request.Put<uint16_t>(requested);
	request.Put<uint16_t>(loopback);
	request.Put<BYTE>(state.bindFlags);
	request.Put<BYTE>(state.multicastTtl);
	request.Put<BYTE>(state.multicastLoop ? 1 : 0);
	int error = SendRequest(request, reply);
	if (!error && (!reply.Get(assigned) || !assigned || (requested && assigned != requested) || reply.cursor != reply.size))
	{
		RemoveSocketRegistration(socket);
		return WSAEPROTONOSUPPORT;
	}
	return error;
}

int BindBackingSocket(SOCKET socket, SocketState &state)
{
	if (state.loopbackBound)
		return 0;
	sockaddr_in local{};
	local.sin_family = AF_INET;
	local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
	SOCKET backing = UdpBackingSocket(socket);
	if (realBind(backing, reinterpret_cast<sockaddr *>(&local), sizeof(local)) == SOCKET_ERROR)
		return WSAGetLastError();
	ResetUdpSocketSignal(socket);
	int length = sizeof(local);
	if (realGetSockName(backing, reinterpret_cast<sockaddr *>(&local), &length) == SOCKET_ERROR)
	{
		state.poisoned = true;
		return WSAGetLastError();
	}
	state.loopbackBound = true;
	state.loopbackPort = ntohs(local.sin_port);
	return 0;
}

int RegisterSocketLocked(SOCKET socket, SocketState &state, uint16_t port)
{
	if (state.poisoned)
		return WSAENETDOWN;
	if (state.registered || state.accepted)
		return 0;
	DWORD reuse = 0, exclusive = 0;
	int size = sizeof(reuse);
	SOCKET backing = UdpBackingSocket(socket);
	if (getsockopt(backing, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<char *>(&reuse), &size) == SOCKET_ERROR)
		return WSAGetLastError();
	size = sizeof(exclusive);
	if (getsockopt(backing, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<char *>(&exclusive), &size) == SOCKET_ERROR)
		return WSAGetLastError();
	state.bindFlags = static_cast<BYTE>((reuse ? 1 : 0) | (exclusive ? 2 : 0));
	uint16_t assigned = 0;
	if (port)
	{
		// Reserve the virtual port before changing the game's Winsock handle.
		int error = RegisterPort(socket, state, port, 0, assigned);
		if (error)
		{
			state.poisoned = error == WSAENETDOWN;
			return error;
		}
	}
	int error = BindBackingSocket(socket, state);
	if (!error)
		error = RegisterPort(socket, state, port, state.loopbackPort, assigned);
	for (size_t i = 0; !error && i < state.groupCount; ++i)
	{
		Frame membership, response;
		membership.Put<BYTE>(8);
		membership.Put<uint64_t>(static_cast<uint64_t>(socket));
		membership.Put<uint32_t>(state.groups[i]);
		membership.Put<BYTE>(1);
		error = SendRequest(membership, response);
	}
	if (error)
	{
		if (assigned)
			RemoveSocketRegistration(socket);
		state.poisoned = state.poisoned || error == WSAENETDOWN;
		return error;
	}
	state.registered = true;
	state.localPort = assigned;
	return 0;
}

int EnsureSocketRegistered(SOCKET socket)
{
	std::lock_guard lock(socketMutex);
	auto found = sockets.find(socket);
	if (found == sockets.end())
		return WSAENOTSOCK;
	if (found->second.internal)
		return WSAEINVAL;
	return RegisterSocketLocked(socket, found->second, 0);
}

int WSAAPI HookBind(SOCKET socket, const sockaddr *address, int length)
{
	SocketState previous;
	if (!FindSocketState(socket, previous))
		return realBind(UdpBackingSocket(socket), address, length);
	if (!address || length < static_cast<int>(sizeof(sockaddr_in)))
		return SocketError(WSAEFAULT);
	if (address->sa_family != AF_INET)
		return SocketError(WSAEAFNOSUPPORT);
	const auto *ipv4 = reinterpret_cast<const sockaddr_in *>(address);
	uint32_t ip = ntohl(ipv4->sin_addr.s_addr);
	std::lock_guard lock(socketMutex);
	auto found = sockets.find(socket);
	if (found == sockets.end())
		return SocketError(WSAENOTSOCK);
	auto &state = found->second;
	if (state.registered || state.internal || state.accepted)
		return SocketError(WSAEINVAL);
	if (IsLoopbackAddress(ip))
	{
		int result = realBind(UdpBackingSocket(socket), address, length);
		if (!result)
		{
			state.internal = true;
			ResetUdpSocketSignal(socket);
		}
		return result;
	}
	if (ip != INADDR_ANY && ip != virtualAddress)
		return SocketError(WSAEADDRNOTAVAIL);
	int error = RegisterSocketLocked(socket, state, ntohs(ipv4->sin_port));
	return error ? SocketError(error) : 0;
}

int WSAAPI HookClose(SOCKET socket)
{
	std::lock_guard lock(socketMutex);
	auto found = sockets.find(socket);
	SOCKET backing = UdpBackingSocket(socket);
	if (backing != socket)
		CancelUdpSocket(socket);
	int result = realClose(backing);
	if (result == SOCKET_ERROR)
	{
		int error = WSAGetLastError();
		ResumeUdpSocket(socket);
		WSASetLastError(error);
		return result;
	}
	int savedError = WSAGetLastError();
	if (found != sockets.end())
	{
		SocketState state = found->second;
		if (backing != socket)
		{
			ForgetUdpAsyncSelect(socket, backing);
			ReleaseUdpSocket(socket);
		}
		sockets.erase(found);
		if (state.registered)
			RemoveSocketRegistration(socket);
	}
	WSASetLastError(savedError);
	return result;
}

int RouteDestination(SOCKET socket, const sockaddr *destination, int length, sockaddr_in &routed, bool tcp)
{
	if (!destination || length < static_cast<int>(sizeof(sockaddr_in)))
		return WSAEFAULT;
	if (destination->sa_family != AF_INET)
		return WSAEAFNOSUPPORT;
	routed = *reinterpret_cast<const sockaddr_in *>(destination);
	uint32_t ip = ntohl(routed.sin_addr.s_addr);
	if (IsLoopbackAddress(ip))
	{
		std::lock_guard lock(socketMutex);
		auto found = sockets.find(socket);
		if (found != sockets.end() && !found->second.registered && !found->second.accepted)
			found->second.internal = true;
		return 0;
	}
	if (!IsOverlayAddress(ip))
		return WSAENETUNREACH;
	if (!routed.sin_port)
		return WSAEADDRNOTAVAIL;
	SocketState state;
	if (!FindSocketState(socket, state))
		return WSAENOTSOCK;
	if (state.internal)
		return WSAEACCES;
	if (tcp && (IsMulticastAddress(ip) || ip == INADDR_BROADCAST || ip == (virtualAddress | ~virtualMask)))
		return WSAEOPNOTSUPP;
	if (!tcp && (ip == INADDR_BROADCAST || ip == (virtualAddress | ~virtualMask)))
	{
		BOOL enabled = FALSE;
		int optionLength = sizeof(enabled);
		if (getsockopt(socket, SOL_SOCKET, SO_BROADCAST, reinterpret_cast<char *>(&enabled), &optionLength) ==
		    SOCKET_ERROR)
			return WSAGetLastError();
		if (!enabled)
			return WSAEACCES;
	}
	int error = EnsureSocketRegistered(socket);
	if (error)
		return error;
	if (ip == (virtualAddress | ~virtualMask))
		ip = INADDR_BROADCAST;
	Frame request, reply;
	request.Put<BYTE>(tcp ? 6 : 3);
	request.Put<uint64_t>(static_cast<uint64_t>(socket));
	request.Put<uint32_t>(ip);
	request.Put<uint16_t>(ntohs(routed.sin_port));
	if (tcp)
		request.Put<BYTE>(state.nonblocking ? 1 : 0);
	else
		request.Put<BYTE>(IsMulticastAddress(ip) && state.multicastTtl == 0 ? 1 : 0);
	error = SendRequest(request, reply);
	uint16_t portal = 0;
	if (!error && (!reply.Get(portal) || !portal || reply.cursor != reply.size))
		error = WSAEPROTONOSUPPORT;
	if (error)
		return error;
	routed.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
	routed.sin_port = htons(portal);
	return 0;
}

void TranslatePortalSource(sockaddr *address, int *length, bool tcp)
{
	if (!address || !length || *length < static_cast<int>(sizeof(sockaddr_in)) || address->sa_family != AF_INET)
		return;
	auto *ipv4 = reinterpret_cast<sockaddr_in *>(address);
	if (!IsLoopbackAddress(ntohl(ipv4->sin_addr.s_addr)))
		return;
	Frame request, reply;
	request.Put<BYTE>(4);
	request.Put<BYTE>(tcp ? 1 : 2);
	request.Put<uint16_t>(ntohs(ipv4->sin_port));
	uint32_t ip = 0;
	uint16_t port = 0;
	if (!SendRequest(request, reply) && reply.Get(ip) && reply.Get(port) && reply.cursor == reply.size && ip && port)
	{
		ipv4->sin_addr.s_addr = htonl(ip);
		ipv4->sin_port = htons(port);
	}
}

void TranslateSourceAddress(SOCKET socket, sockaddr *address, int *length, bool tcp)
{
	SocketState state;
	if (FindSocketState(socket, state) && !state.internal && (state.registered || state.accepted))
		TranslatePortalSource(address, length, tcp);
}

int SetDatagramPeer(SOCKET socket, SocketState &state, uint32_t address, uint16_t port)
{
	if (state.type != SOCK_DGRAM || !state.registered)
		return 0;
	Frame request, reply;
	request.Put<BYTE>(12);
	request.Put<uint64_t>(static_cast<uint64_t>(socket));
	request.Put<uint32_t>(address);
	request.Put<uint16_t>(port);
	int error = SendRequest(request, reply);
	if (error)
		state.poisoned = true;
	return error;
}

int WSAAPI HookConnect(SOCKET socket, const sockaddr *address, int length)
{
	std::unique_lock connectLock(socketMutex);
	SocketState state;
	if (!FindSocketState(socket, state))
	{
		connectLock.unlock();
		return realConnect(UdpBackingSocket(socket), address, length);
	}
	if (state.type != SOCK_DGRAM)
		connectLock.unlock();
	if (address && length >= static_cast<int>(sizeof(sockaddr)) && address->sa_family == AF_UNSPEC &&
	    state.type == SOCK_DGRAM)
	{
		int result = realConnect(UdpBackingSocket(socket), address, length);
		if (!result)
		{
			std::lock_guard lock(socketMutex);
			auto found = sockets.find(socket);
			if (found != sockets.end())
			{
				found->second.connected = false;
				int error = SetDatagramPeer(socket, found->second, 0, 0);
				if (error)
					return SocketError(error);
			}
		}
		return result;
	}
	sockaddr_in routed{};
	int error = RouteDestination(socket, address, length, routed, state.type == SOCK_STREAM);
	if (error)
		return SocketError(error);
	int result = realConnect(UdpBackingSocket(socket), reinterpret_cast<sockaddr *>(&routed), sizeof(routed));
	int connectError = result == SOCKET_ERROR ? WSAGetLastError() : 0;
	if (!result || connectError == WSAEWOULDBLOCK || connectError == WSAEINPROGRESS)
	{
		const auto *original = reinterpret_cast<const sockaddr_in *>(address);
		std::lock_guard lock(socketMutex);
		auto found = sockets.find(socket);
		if (found != sockets.end())
		{
			found->second.connected = true;
			found->second.peerAddress = ntohl(original->sin_addr.s_addr);
			found->second.peerPort = ntohs(original->sin_port);
			if (IsLoopbackAddress(found->second.peerAddress) && !found->second.registered)
				found->second.internal = true;
			error = SetDatagramPeer(socket, found->second, found->second.peerAddress, found->second.peerPort);
			if (error)
				return SocketError(error);
		}
	}
	if (connectError)
		WSASetLastError(connectError);
	return result;
}

int WSAAPI HookWSAConnect(SOCKET socket, const sockaddr *address, int length, LPWSABUF caller, LPWSABUF callee,
                          LPQOS sendQos, LPQOS receiveQos)
{
	SocketState state;
	if (!FindSocketState(socket, state))
		return realWSAConnect(UdpBackingSocket(socket), address, length, caller, callee, sendQos, receiveQos);
	if (caller || callee || sendQos || receiveQos)
		return SocketError(WSAEOPNOTSUPP);
	return HookConnect(socket, address, length);
}

int WSAAPI HookListen(SOCKET socket, int backlog)
{
	SocketState state;
	if (!FindSocketState(socket, state) || state.internal)
		return realListen(UdpBackingSocket(socket), backlog);
	if (state.type != SOCK_STREAM)
		return SocketError(WSAEOPNOTSUPP);
	int error = EnsureSocketRegistered(socket);
	if (error)
		return SocketError(error);
	int result = realListen(UdpBackingSocket(socket), backlog);
	if (result)
		return result;
	Frame request, reply;
	request.Put<BYTE>(5);
	request.Put<uint64_t>(static_cast<uint64_t>(socket));
	request.Put<BYTE>(1);
	error = SendRequest(request, reply);
	return error ? SocketError(error) : 0;
}

SOCKET TrackAcceptedSocket(SOCKET listener, SOCKET accepted, sockaddr *address, int *length)
{
	if (accepted == INVALID_SOCKET)
		return accepted;
	SocketState parent;
	if (!FindSocketState(listener, parent))
		return accepted;
	SocketState state = parent;
	state.registered = false;
	state.accepted = !state.internal;
	state.connected = true;
	sockaddr_in peer{};
	int peerLength = sizeof(peer);
	if (realGetPeerName(accepted, reinterpret_cast<sockaddr *>(&peer), &peerLength))
	{
		int error = WSAGetLastError();
		realClose(accepted);
		SocketError(error);
		return INVALID_SOCKET;
	}
	TranslateSourceAddress(listener, reinterpret_cast<sockaddr *>(&peer), &peerLength, true);
	state.peerAddress = ntohl(peer.sin_addr.s_addr);
	state.peerPort = ntohs(peer.sin_port);
	try
	{
		std::lock_guard lock(socketMutex);
		sockets.emplace(accepted, state);
	}
	catch (...)
	{
		realClose(accepted);
		SocketError(WSAENOBUFS);
		return INVALID_SOCKET;
	}
	if (address && length && *length >= static_cast<int>(sizeof(peer)))
		memcpy(address, &peer, sizeof(peer));
	return accepted;
}

SOCKET WSAAPI HookAccept(SOCKET socket, sockaddr *address, int *length)
{
	return TrackAcceptedSocket(socket, realAccept(UdpBackingSocket(socket), address, length), address, length);
}
SOCKET WSAAPI HookWSAAccept(SOCKET socket, sockaddr *address, LPINT length, LPCONDITIONPROC condition, DWORD_PTR data)
{
	SocketState state;
	if (FindSocketState(socket, state) && !state.internal && condition)
	{
		SocketError(WSAEOPNOTSUPP);
		return INVALID_SOCKET;
	}
	return TrackAcceptedSocket(socket, realWSAAccept(UdpBackingSocket(socket), address, length, condition, data),
	                           address, length);
}

int CheckConnectedDatagram(SOCKET socket, uint64_t length)
{
	SocketState state;
	if (!FindSocketState(socket, state))
		return 0;
	if (state.poisoned)
		return WSAENETDOWN;
	if (state.type != SOCK_DGRAM || state.internal)
		return 0;
	if (length > MaximumDatagram)
		return WSAEMSGSIZE;
	if (!state.connected)
		return WSAENOTCONN;
	sockaddr_in destination{};
	destination.sin_family = AF_INET;
	destination.sin_addr.s_addr = htonl(state.peerAddress);
	destination.sin_port = htons(state.peerPort);
	sockaddr_in ignored{};
	return RouteDestination(socket, reinterpret_cast<sockaddr *>(&destination), sizeof(destination), ignored, false);
}

int WSAAPI HookSend(SOCKET socket, const char *bytes, int length, int flags)
{
	if (length < 0)
		return SocketError(WSAEINVAL);
	int error = CheckConnectedDatagram(socket, static_cast<uint64_t>(length));
	return error ? SocketError(error) : realSend(UdpBackingSocket(socket), bytes, length, flags);
}

int WSAAPI HookSendTo(SOCKET socket, const char *bytes, int length, int flags, const sockaddr *destination,
                      int destinationLength)
{
	SocketState state;
	if (!FindSocketState(socket, state))
		return realSendTo(UdpBackingSocket(socket), bytes, length, flags, destination, destinationLength);
	if (state.type == SOCK_STREAM || !destination)
		return HookSend(socket, bytes, length, flags);
	if (length < 0)
		return SocketError(WSAEINVAL);
	if (!state.internal && static_cast<uint64_t>(length) > MaximumDatagram)
		return SocketError(WSAEMSGSIZE);
	sockaddr_in routed{};
	int error = RouteDestination(socket, destination, destinationLength, routed, false);
	return error ? SocketError(error)
	             : realSendTo(UdpBackingSocket(socket), bytes, length, flags, reinterpret_cast<sockaddr *>(&routed),
	                          sizeof(routed));
}

int WSAAPI HookRecvFrom(SOCKET socket, char *bytes, int length, int flags, sockaddr *source, int *sourceLength)
{
	int result = realRecvFrom(UdpBackingSocket(socket), bytes, length, flags, source, sourceLength);
	int error = WSAGetLastError();
	if (result >= 0 || error == WSAEMSGSIZE)
		TranslateSourceAddress(socket, source, sourceLength, false);
	WSASetLastError(error);
	return result;
}

int GetBufferLength(LPWSABUF buffers, DWORD count, uint64_t &length)
{
	if (!buffers || !count)
		return WSAEFAULT;
	length = 0;
	for (DWORD i = 0; i < count; ++i)
		length += buffers[i].len;
	return 0;
}

int WSAAPI HookWSASend(SOCKET socket, LPWSABUF buffers, DWORD count, LPDWORD bytes, DWORD flags,
                       LPWSAOVERLAPPED overlap, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
	uint64_t length = 0;
	int error = GetBufferLength(buffers, count, length);
	if (!error)
		error = CheckConnectedDatagram(socket, length);
	if (error)
		return SocketError(error);
	SOCKET backing = UdpBackingSocket(socket);
	if (backing != socket && overlap)
		return SubmitUdpIo(socket, overlap, completion, nullptr, nullptr, [&](SOCKET native, LPWSAOVERLAPPED op) {
			return realWSASend(native, buffers, count, bytes, flags, op, nullptr);
		});
	return realWSASend(backing, buffers, count, bytes, flags, overlap, completion);
}

int WSAAPI HookWSASendTo(SOCKET socket, LPWSABUF buffers, DWORD count, LPDWORD bytes, DWORD flags,
                         const sockaddr *destination, int destinationLength, LPWSAOVERLAPPED overlap,
                         LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
	SocketState state;
	if (!FindSocketState(socket, state))
		return realWSASendTo(UdpBackingSocket(socket), buffers, count, bytes, flags, destination, destinationLength,
		                     overlap, completion);
	if (state.type == SOCK_STREAM || !destination)
		return HookWSASend(socket, buffers, count, bytes, flags, overlap, completion);
	uint64_t length = 0;
	int error = GetBufferLength(buffers, count, length);
	if (!error && !state.internal && length > MaximumDatagram)
		error = WSAEMSGSIZE;
	sockaddr_in routed{};
	if (!error)
		error = RouteDestination(socket, destination, destinationLength, routed, false);
	if (error)
		return SocketError(error);
	SOCKET backing = UdpBackingSocket(socket);
	if (backing != socket && overlap)
		return SubmitUdpIo(socket, overlap, completion, nullptr, nullptr, [&](SOCKET native, LPWSAOVERLAPPED op) {
			return realWSASendTo(native, buffers, count, bytes, flags, reinterpret_cast<sockaddr *>(&routed),
			                     sizeof(routed), op, nullptr);
		});
	return realWSASendTo(backing, buffers, count, bytes, flags, reinterpret_cast<sockaddr *>(&routed), sizeof(routed),
	                     overlap, completion);
}

int WSAAPI HookWSARecvFrom(SOCKET socket, LPWSABUF buffers, DWORD count, LPDWORD bytes, LPDWORD flags, sockaddr *source,
                           LPINT sourceLength, LPWSAOVERLAPPED overlap, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
	SocketState state;
	bool translate = source && FindSocketState(socket, state) && state.registered && !state.internal;
	SOCKET backing = UdpBackingSocket(socket);
	if (backing != socket && overlap)
		return SubmitUdpIo(socket, overlap, completion, translate ? source : nullptr, sourceLength,
		                   [&](SOCKET native, LPWSAOVERLAPPED op) {
			                   return realWSARecvFrom(native, buffers, count, bytes, flags, source, sourceLength, op,
			                                          nullptr);
		                   });
	int result = realWSARecvFrom(backing, buffers, count, bytes, flags, source, sourceLength, overlap, completion);
	int error = WSAGetLastError();
	if (translate && (!result || error == WSAEMSGSIZE))
		TranslatePortalSource(source, sourceLength, false);
	WSASetLastError(error);
	return result;
}

int WSAAPI HookGetSockName(SOCKET socket, sockaddr *address, int *length)
{
	int result = realGetSockName(UdpBackingSocket(socket), address, length);
	if (result)
		return result;
	SocketState state;
	if (address && length && *length >= static_cast<int>(sizeof(sockaddr_in)) && FindSocketState(socket, state) &&
	    (state.registered || state.accepted))
	{
		auto *ipv4 = reinterpret_cast<sockaddr_in *>(address);
		ipv4->sin_addr.s_addr = htonl(virtualAddress);
		ipv4->sin_port = htons(state.localPort);
	}
	return result;
}

int WSAAPI HookGetPeerName(SOCKET socket, sockaddr *address, int *length)
{
	int result = realGetPeerName(UdpBackingSocket(socket), address, length);
	if (result)
		return result;
	SocketState state;
	if (address && length && *length >= static_cast<int>(sizeof(sockaddr_in)) && FindSocketState(socket, state) &&
	    state.connected && !state.internal)
	{
		auto *ipv4 = reinterpret_cast<sockaddr_in *>(address);
		ipv4->sin_addr.s_addr = htonl(state.peerAddress);
		ipv4->sin_port = htons(state.peerPort);
	}
	return result;
}

bool IsMulticastInterface(uint32_t address)
{
	return address == 0 || address == virtualAddress || address == VirtualAdapterIndex;
}

int ReconnectMulticastSocket(SOCKET socket, const SocketState &state)
{
	if (!state.connected || !IsMulticastAddress(state.peerAddress))
		return 0;
	sockaddr_in peer{}, routed{};
	peer.sin_family = AF_INET;
	peer.sin_addr.s_addr = htonl(state.peerAddress);
	peer.sin_port = htons(state.peerPort);
	int error = RouteDestination(socket, reinterpret_cast<sockaddr *>(&peer), sizeof(peer), routed, false);
	if (!error && realConnect(UdpBackingSocket(socket), reinterpret_cast<sockaddr *>(&routed), sizeof(routed)))
		error = WSAGetLastError();
	return error;
}

int SetMulticastOption(SOCKET socket, SocketState &state, int option, const char *value, int length)
{
	SOCKET backing = UdpBackingSocket(socket);
	DWORD previous = option == IP_MULTICAST_TTL ? state.multicastTtl : (state.multicastLoop ? 1 : 0);
	if (realSetSockOpt(backing, IPPROTO_IP, option, value, length))
		return SOCKET_ERROR;
	DWORD normalized = 0;
	int size = sizeof(normalized);
	if (getsockopt(backing, IPPROTO_IP, option, reinterpret_cast<char *>(&normalized), &size))
		return SOCKET_ERROR;
	if (option == IP_MULTICAST_TTL)
		state.multicastTtl = static_cast<BYTE>(normalized);
	else
		state.multicastLoop = normalized != 0;
	if (!state.registered)
		return 0;
	int error = option == IP_MULTICAST_TTL ? ReconnectMulticastSocket(socket, state) : 0;
	if (!error)
	{
		Frame request, reply;
		request.Put<BYTE>(11);
		request.Put<uint64_t>(static_cast<uint64_t>(socket));
		request.Put<BYTE>(state.multicastTtl);
		request.Put<BYTE>(state.multicastLoop ? 1 : 0);
		error = SendRequest(request, reply);
	}
	if (!error)
		return 0;
	if (option == IP_MULTICAST_TTL)
		state.multicastTtl = static_cast<BYTE>(previous);
	else
		state.multicastLoop = previous != 0;
	if (realSetSockOpt(backing, IPPROTO_IP, option, reinterpret_cast<const char *>(&previous), sizeof(previous)) ||
	    (option == IP_MULTICAST_TTL && ReconnectMulticastSocket(socket, state)) || error == WSAENETDOWN)
		state.poisoned = true;
	return SocketError(error);
}

int WSAAPI HookSetSockOpt(SOCKET socket, int level, int option, const char *value, int length)
{
	SocketState state;
	if (!FindSocketState(socket, state) || state.internal || level != IPPROTO_IP)
		return realSetSockOpt(UdpBackingSocket(socket), level, option, value, length);
	if (option == IP_MULTICAST_TTL || option == IP_MULTICAST_LOOP)
	{
		std::lock_guard lock(socketMutex);
		auto found = sockets.find(socket);
		if (found == sockets.end())
			return SocketError(WSAENOTSOCK);
		if (found->second.type == SOCK_DGRAM)
			return SetMulticastOption(socket, found->second, option, value, length);
		return realSetSockOpt(UdpBackingSocket(socket), level, option, value, length);
	}
	if (option == IP_ADD_MEMBERSHIP || option == IP_DROP_MEMBERSHIP)
	{
		if (!value || length < static_cast<int>(sizeof(ip_mreq)))
			return SocketError(WSAEFAULT);
		const auto *membership = reinterpret_cast<const ip_mreq *>(value);
		uint32_t group = ntohl(membership->imr_multiaddr.s_addr);
		uint32_t adapter = ntohl(membership->imr_interface.s_addr);
		if (!IsMulticastAddress(group) || !IsMulticastInterface(adapter))
			return SocketError(WSAEADDRNOTAVAIL);
		std::lock_guard lock(socketMutex);
		auto found = sockets.find(socket);
		if (found == sockets.end())
			return SocketError(WSAENOTSOCK);
		auto &current = found->second;
		if (current.type != SOCK_DGRAM)
			return SocketError(WSAEOPNOTSUPP);
		size_t index = 0;
		while (index < current.groupCount && current.groups[index] != group)
			++index;
		bool join = option == IP_ADD_MEMBERSHIP;
		if (join && index < current.groupCount)
			return SocketError(WSAEINVAL);
		if (!join && index == current.groupCount)
			return SocketError(WSAEADDRNOTAVAIL);
		if (join && index == current.groupCount && current.groupCount == current.groups.size())
			return SocketError(WSAENOBUFS);
		if (current.registered)
		{
			Frame request, reply;
			request.Put<BYTE>(8);
			request.Put<uint64_t>(static_cast<uint64_t>(socket));
			request.Put<uint32_t>(group);
			request.Put<BYTE>(join ? 1 : 0);
			int error = SendRequest(request, reply);
			if (error)
				return SocketError(error);
		}
		if (join && index == current.groupCount)
			current.groups[current.groupCount++] = group;
		if (!join && index < current.groupCount)
		{
			current.groups[index] = current.groups[--current.groupCount];
		}
		return 0;
	}
	if (option == IP_MULTICAST_IF)
	{
		if (state.type != SOCK_DGRAM)
			return realSetSockOpt(UdpBackingSocket(socket), level, option, value, length);
		if (!value || length < static_cast<int>(sizeof(in_addr)))
			return SocketError(WSAEFAULT);
		uint32_t address = ntohl(reinterpret_cast<const in_addr *>(value)->s_addr);
		if (!IsMulticastInterface(address))
			return SocketError(WSAEADDRNOTAVAIL);
		std::lock_guard lock(socketMutex);
		auto found = sockets.find(socket);
		if (found == sockets.end())
			return SocketError(WSAENOTSOCK);
		found->second.multicastInterface = reinterpret_cast<const in_addr *>(value)->s_addr;
		return 0;
	}
	if (option == IP_ADD_SOURCE_MEMBERSHIP || option == IP_DROP_SOURCE_MEMBERSHIP || option == IP_BLOCK_SOURCE ||
	    option == IP_UNBLOCK_SOURCE || option == IP_UNICAST_IF)
		return SocketError(WSAEOPNOTSUPP);
	return realSetSockOpt(UdpBackingSocket(socket), level, option, value, length);
}

void SetNonblockingState(SOCKET socket, bool enabled)
{
	std::lock_guard lock(socketMutex);
	auto found = sockets.find(socket);
	if (found != sockets.end())
		found->second.nonblocking = enabled;
}
int WSAAPI HookIoctlSocket(SOCKET socket, long command, u_long *value)
{
	int result = realIoctlSocket(UdpBackingSocket(socket), command, value);
	if (!result && command == FIONBIO && value)
		SetNonblockingState(socket, *value != 0);
	return result;
}
int WSAAPI HookWSAEventSelect(SOCKET socket, WSAEVENT event, long events)
{
	SOCKET backing = UdpBackingSocket(socket);
	int result = realWSAEventSelect(backing, event, events);
	if (!result)
	{
		int error = WSAGetLastError();
		if (backing != socket)
			ForgetUdpAsyncSelect(socket, backing);
		if (events)
			SetNonblockingState(socket, true);
		WSASetLastError(error);
	}
	return result;
}
int WSAAPI HookWSAAsyncSelect(SOCKET socket, HWND window, unsigned int message, long events)
{
	int result = UdpAsyncSelect(socket, window, message, events, realWSAAsyncSelect);
	if (!result && events)
		SetNonblockingState(socket, true);
	return result;
}

int WSAAPI HookWSAIoctl(SOCKET socket, DWORD code, LPVOID input, DWORD inputLength, LPVOID output, DWORD outputLength,
                        LPDWORD returned, LPWSAOVERLAPPED overlap, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
	SocketState state;
	SOCKET backing = UdpBackingSocket(socket);
	if (backing != socket && (code == SIO_BASE_HANDLE || code == SIO_BSP_HANDLE || code == SIO_BSP_HANDLE_SELECT ||
	                          code == SIO_BSP_HANDLE_POLL || code == SIO_GET_EXTENSION_FUNCTION_POINTER ||
	                          code == SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER))
		return SocketError(WSAEOPNOTSUPP);
	auto issue = [&] {
		if (backing != socket && overlap)
			return SubmitUdpIo(socket, overlap, completion, nullptr, nullptr, [&](SOCKET native, LPWSAOVERLAPPED op) {
				return realWSAIoctl(native, code, input, inputLength, output, outputLength, returned, op, nullptr);
			});
		return realWSAIoctl(backing, code, input, inputLength, output, outputLength, returned, overlap, completion);
	};
	if (!FindSocketState(socket, state) || state.internal)
		return issue();
	if (code == SIO_GET_EXTENSION_FUNCTION_POINTER || code == SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER ||
	    code == SIO_ADDRESS_LIST_CHANGE)
		return SocketError(WSAEOPNOTSUPP);
	if (code == SIO_GET_INTERFACE_LIST || code == SIO_ADDRESS_LIST_QUERY || code == SIO_ROUTING_INTERFACE_QUERY)
	{
		if (overlap || completion)
			return SocketError(WSAEOPNOTSUPP);
		if (!returned)
			return SocketError(WSAEFAULT);
		if (code == SIO_GET_INTERFACE_LIST)
		{
			*returned = sizeof(INTERFACE_INFO);
			if (!output || outputLength < sizeof(INTERFACE_INFO))
				return SocketError(WSAEFAULT);
			auto *info = static_cast<INTERFACE_INFO *>(output);
			ZeroMemory(info, sizeof(*info));
			info->iiFlags = IFF_UP | IFF_BROADCAST | IFF_MULTICAST;
			info->iiAddress.AddressIn.sin_family = AF_INET;
			info->iiAddress.AddressIn.sin_addr.s_addr = htonl(virtualAddress);
			info->iiNetmask.AddressIn.sin_family = AF_INET;
			info->iiNetmask.AddressIn.sin_addr.s_addr = htonl(virtualMask);
			info->iiBroadcastAddress.AddressIn.sin_family = AF_INET;
			info->iiBroadcastAddress.AddressIn.sin_addr.s_addr = htonl(virtualAddress | ~virtualMask);
			return 0;
		}
		if (code == SIO_ROUTING_INTERFACE_QUERY)
		{
			if (!input || inputLength < sizeof(sockaddr_in))
				return SocketError(WSAEFAULT);
			auto *destination = static_cast<sockaddr_in *>(input);
			if (destination->sin_family != AF_INET || !IsOverlayAddress(ntohl(destination->sin_addr.s_addr)))
				return SocketError(WSAENETUNREACH);
			*returned = sizeof(sockaddr_in);
			if (!output || outputLength < sizeof(sockaddr_in))
				return SocketError(WSAEFAULT);
			auto *address = static_cast<sockaddr_in *>(output);
			*address = {};
			address->sin_family = AF_INET;
			address->sin_addr.s_addr = htonl(virtualAddress);
			return 0;
		}
		constexpr DWORD required = sizeof(SOCKET_ADDRESS_LIST) + sizeof(sockaddr_in);
		*returned = required;
		if (!output || outputLength < required)
			return SocketError(WSAEFAULT);
		ZeroMemory(output, required);
		auto *list = static_cast<SOCKET_ADDRESS_LIST *>(output);
		auto *address = reinterpret_cast<sockaddr_in *>(static_cast<BYTE *>(output) + sizeof(SOCKET_ADDRESS_LIST));
		address->sin_family = AF_INET;
		address->sin_addr.s_addr = htonl(virtualAddress);
		list->iAddressCount = 1;
		list->Address[0].lpSockaddr = reinterpret_cast<sockaddr *>(address);
		list->Address[0].iSockaddrLength = sizeof(*address);
		return 0;
	}
	int result = issue();
	if (!result && code == FIONBIO && input && inputLength >= sizeof(u_long))
		SetNonblockingState(socket, *static_cast<u_long *>(input) != 0);
	return result;
}

bool InitializeHooks()
{
	wchar_t pipeName[256]{};
	wchar_t token[33]{};
	DWORD pipeLength = GetEnvironmentVariableW(L"TURBINE_PIPE", pipeName, static_cast<DWORD>(std::size(pipeName)));
	if (!pipeLength || pipeLength >= std::size(pipeName) || wcschr(pipeName, L'\\') || wcschr(pipeName, L'/'))
		return false;
	if (GetEnvironmentVariableW(L"TURBINE_TOKEN", token, static_cast<DWORD>(std::size(token))) != 32)
		return false;
	wchar_t fullName[280]{};
	if (swprintf_s(fullName, L"\\\\.\\pipe\\%s", pipeName) < 0)
		return false;
	if (!WaitNamedPipeW(fullName, 10000))
		return false;
	pipeHandle = CreateFileW(fullName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
	                         FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
	if (pipeHandle == INVALID_HANDLE_VALUE)
		return false;
	Frame request, reply;
	request.Put<BYTE>(1);
	for (size_t i = 0; i < 32; i += 2)
	{
		auto nibble = [](wchar_t value) -> int {
			if (value >= L'0' && value <= L'9')
				return value - L'0';
			if (value >= L'a' && value <= L'f')
				return value - L'a' + 10;
			if (value >= L'A' && value <= L'F')
				return value - L'A' + 10;
			return -1;
		};
		int high = nibble(token[i]), low = nibble(token[i + 1]);
		if (high < 0 || low < 0)
			return false;
		request.Put<BYTE>(static_cast<BYTE>((high << 4) | low));
	}
	if (SendRequest(request, reply) || !reply.Get(virtualAddress) || !reply.Get(virtualMask) ||
	    reply.cursor != reply.size || (!virtualAddress != !virtualMask))
		return false;
	if (DetourTransactionBegin() != NO_ERROR)
		return false;
	LONG error = DetourUpdateThread(GetCurrentThread());
	if (!error)
		error = AttachChildHooks();
	if (!error)
		error = AttachConnectionHelpers();
	if (!error && !virtualMask)
		error = AttachLanHooks();
	if (!error && virtualMask)
	{
		error = AttachUdpSocketHooks();
		if (!error)
			error = AttachUdpForwardHooks();
#define HOOK(name)                                                                                                     \
	if (!error)                                                                                                        \
	error = Attach(real##name, Hook##name)
		HOOK(Socket);
		HOOK(WSAStartup);
		HOOK(WSACleanup);
		HOOK(WSASocketA);
		HOOK(WSASocketW);
		HOOK(Bind);
		HOOK(Close);
		HOOK(Connect);
		HOOK(WSAConnect);
		HOOK(Listen);
		HOOK(Accept);
		HOOK(WSAAccept);
		HOOK(Send);
		HOOK(SendTo);
		HOOK(RecvFrom);
		HOOK(WSASend);
		HOOK(WSASendTo);
		HOOK(WSARecvFrom);
		HOOK(GetSockName);
		HOOK(GetPeerName);
		HOOK(SetSockOpt);
		HOOK(IoctlSocket);
		HOOK(WSAIoctl);
		HOOK(WSAEventSelect);
		HOOK(WSAAsyncSelect);
#undef HOOK
		if (!error)
			error = AttachAdapterHooks();
	}
	if (error)
	{
		DetourTransactionAbort();
		return false;
	}
	return DetourTransactionCommit() == NO_ERROR;
}
}

void TranslateUdpSource(sockaddr *address, int *length)
{
	TranslatePortalSource(address, length, false);
}

bool GetVirtualMulticastInterface(SOCKET socket, uint32_t &value)
{
	SocketState state;
	if (!FindSocketState(socket, state) || state.internal || state.type != SOCK_DGRAM)
		return false;
	value = state.multicastInterface;
	return true;
}

int CloseGameUdp(SOCKET socket)
{
	return HookClose(socket);
}

int SocketError(int error)
{
	WSASetLastError(error);
	return SOCKET_ERROR;
}
int ChildRequest(BYTE opcode, const BYTE *data, uint32_t size, BYTE *output, uint32_t &outputSize)
{
	if (size >= NativeFrameCapacity)
		return WSAEINVAL;
	Frame request, reply;
	request.Put<BYTE>(opcode);
	if (size)
		memcpy(request.data.data() + request.size, data, size);
	request.size += size;
	int error = SendRequest(request, reply);
	if (error)
		return error;
	uint32_t bytes = reply.size - reply.cursor;
	if (bytes > outputSize || (bytes && !output))
		return WSAEINVAL;
	if (bytes)
		memcpy(output, reply.data.data() + reply.cursor, bytes);
	outputSize = bytes;
	return 0;
}
bool IsOverlayAddress(uint32_t address)
{
	return address == INADDR_BROADCAST || (address & 0xf0000000u) == 0xe0000000u ||
	       (address & virtualMask) == (virtualAddress & virtualMask);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
	if (reason == DLL_PROCESS_DETACH)
		StopUdpHooks();
	if (reason != DLL_PROCESS_ATTACH)
		return TRUE;
	if (DetourIsHelperProcess())
		return TRUE;
	DetourRestoreAfterWith();
	DisableThreadLibraryCalls(instance);
	wchar_t eventName[128]{};
	DWORD length = GetEnvironmentVariableW(L"TURBINE_READY", eventName, static_cast<DWORD>(std::size(eventName)));
	if (!length || length >= std::size(eventName))
		return FALSE;
	HANDLE ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, eventName);
	if (!ready)
		return FALSE;
	BOOL result = FALSE;
	try
	{
		if (InitializeHooks())
			result = SetEvent(ready);
	}
	catch (...)
	{
	}
	CloseHandle(ready);
	if (!result && pipeHandle != INVALID_HANDLE_VALUE)
	{
		CloseHandle(pipeHandle);
		pipeHandle = INVALID_HANDLE_VALUE;
	}
	return result;
}
