#include "Hook.h"
#include <mswsock.h>
#include <windns.h>
#include <algorithm>
#include <memory>
#include <vector>

namespace
{
struct LanAddress
{
	SOCKADDR_INET address{};
	NET_IFINDEX index{};
	ULONG metric{};
	BYTE prefix{};
	bool loopback{};
};

bool HasLanLink(NET_IFINDEX index, const MIB_IFSTACK_TABLE *stack, unsigned depth = 0)
{
	MIB_IF_ROW2 row{};
	row.InterfaceIndex = index;
	if (depth > 8 || GetIfEntry2(&row) || row.OperStatus != IfOperStatusUp)
		return false;
	if (row.InterfaceAndOperStatusFlags.HardwareInterface &&
	    (row.Type == IF_TYPE_ETHERNET_CSMACD || row.Type == IF_TYPE_IEEE80211))
		return true;
	if (stack && !row.InterfaceAndOperStatusFlags.FilterInterface && row.TunnelType == TUNNEL_TYPE_NONE)
		for (ULONG i = 0; i < stack->NumEntries; ++i)
			if (stack->Table[i].HigherLayerInterfaceIndex == index &&
			    HasLanLink(stack->Table[i].LowerLayerInterfaceIndex, stack, depth + 1))
				return true;
	return false;
}

std::vector<LanAddress> ReadAddresses()
{
	ULONG size = 16384, error;
	std::vector<BYTE> buffer;
	do
	{
		buffer.resize(size);
		error =
		    GetAdaptersAddresses(AF_UNSPEC, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
		                         nullptr, reinterpret_cast<IP_ADAPTER_ADDRESSES *>(buffer.data()), &size);
	} while (error == ERROR_BUFFER_OVERFLOW);
	if (error)
		return {};
	MIB_IFSTACK_TABLE *raw = nullptr;
	GetIfStackTable(&raw);
	std::unique_ptr<MIB_IFSTACK_TABLE, decltype(&FreeMibTable)> stack(raw, FreeMibTable);
	std::vector<LanAddress> result;
	for (auto *a = reinterpret_cast<IP_ADAPTER_ADDRESSES *>(buffer.data()); a; a = a->Next)
	{
		bool loopback = a->IfType == IF_TYPE_SOFTWARE_LOOPBACK;
		if (a->OperStatus != IfOperStatusUp ||
		    (!loopback && !HasLanLink(a->IfIndex ? a->IfIndex : a->Ipv6IfIndex, stack.get())))
			continue;
		for (auto *u = a->FirstUnicastAddress; u; u = u->Next)
		{
			const auto &sa = u->Address;
			if (!sa.lpSockaddr || sa.iSockaddrLength <= 0 || size_t(sa.iSockaddrLength) > sizeof(SOCKADDR_INET) ||
			    u->DadState < IpDadStateDeprecated || !u->OnLinkPrefixLength)
				continue;
			bool v4 = sa.lpSockaddr->sa_family == AF_INET;
			if (!v4 && sa.lpSockaddr->sa_family != AF_INET6)
				continue;
			LanAddress item{};
			memcpy(&item.address, sa.lpSockaddr, sa.iSockaddrLength);
			item.index = v4 ? a->IfIndex : a->Ipv6IfIndex;
			item.metric = v4 ? a->Ipv4Metric : a->Ipv6Metric;
			item.prefix = u->OnLinkPrefixLength;
			item.loopback = loopback;
			result.push_back(item);
		}
	}
	std::stable_sort(result.begin(), result.end(), [](const auto &a, const auto &b) { return a.metric < b.metric; });
	return result;
}

const std::vector<LanAddress> &Addresses()
{
	// Capture the LAN at first socket use, outside the DLL loader lock.
	static const auto addresses = ReadAddresses();
	return addresses;
}

bool IsIp(int family)
{
	return family == AF_INET || family == AF_INET6;
}

const BYTE *AddressBytes(const SOCKADDR_INET &ip)
{
	return ip.si_family == AF_INET ? reinterpret_cast<const BYTE *>(&ip.Ipv4.sin_addr) : ip.Ipv6.sin6_addr.u.Byte;
}

bool SamePrefix(const SOCKADDR_INET &ip, const LanAddress &local, unsigned bits)
{
	if (ip.si_family != local.address.si_family || bits > (ip.si_family == AF_INET ? 32u : 128u))
		return false;
	auto a = AddressBytes(ip), b = AddressBytes(local.address);
	return !memcmp(a, b, bits / 8) && (!(bits % 8) || ((a[bits / 8] ^ b[bits / 8]) >> (8 - bits % 8)) == 0);
}

bool IsUnspecified(const SOCKADDR_INET &ip)
{
	return ip.si_family == AF_INET ? ip.Ipv4.sin_addr.s_addr == INADDR_ANY
	                               : IN6_IS_ADDR_UNSPECIFIED(&ip.Ipv6.sin6_addr);
}

bool IsOnLan(const SOCKADDR_INET &ip)
{
	if (ip.si_family == AF_INET && (ntohl(ip.Ipv4.sin_addr.s_addr) >> 24) == 127)
		return true;
	if (ip.si_family == AF_INET6 && IN6_IS_ADDR_LOOPBACK(&ip.Ipv6.sin6_addr))
		return true;
	if (ip.si_family == AF_INET6 && IN6_IS_ADDR_V4MAPPED(&ip.Ipv6.sin6_addr))
	{
		SOCKADDR_INET mapped{};
		mapped.Ipv4.sin_family = AF_INET;
		memcpy(&mapped.Ipv4.sin_addr, &ip.Ipv6.sin6_addr.u.Byte[12], 4);
		return IsOnLan(mapped);
	}
	for (const auto &a : Addresses())
	{
		if (a.loopback || !SamePrefix(ip, a, a.prefix))
			continue;
		if (ip.si_family == AF_INET6 && ip.Ipv6.sin6_scope_id && ip.Ipv6.sin6_scope_id != a.index)
			continue;
		MIB_IPFORWARD_ROW2 route{};
		SOCKADDR_INET source{};
		if (!GetBestRoute2(nullptr, 0, nullptr, &ip, 0, &route, &source) && route.InterfaceIndex == a.index &&
		    route.DestinationPrefix.PrefixLength && IsUnspecified(route.NextHop))
			return true;
	}
	return false;
}

bool ReadAddress(const sockaddr *sa, int length, SOCKADDR_INET &ip)
{
	if (!sa || length < int(sizeof(ADDRESS_FAMILY)) || !IsIp(sa->sa_family))
		return false;
	size_t needed = sa->sa_family == AF_INET ? sizeof(sockaddr_in) : sizeof(sockaddr_in6);
	if (length < int(needed))
		return false;
	memcpy(&ip, sa, needed);
	return true;
}

const LanAddress *DiscoveryInterface(SOCKET socket, const SOCKADDR_INET &ip, bool multicast)
{
	int family = ip.si_family;
	SOCKADDR_INET source{};
	int size = sizeof(source);
	bool bound = !getsockname(socket, reinterpret_cast<sockaddr *>(&source), &size) && source.si_family == family &&
	             !IsUnspecified(source);
	DWORD requested = 0;
	NET_IFINDEX index = family == AF_INET6 ? ip.Ipv6.sin6_scope_id : 0;
	if (multicast)
	{
		size = sizeof(requested);
		if (getsockopt(socket, family == AF_INET ? IPPROTO_IP : IPPROTO_IPV6,
		               family == AF_INET ? IP_MULTICAST_IF : IPV6_MULTICAST_IF, reinterpret_cast<char *>(&requested),
		               &size))
			return nullptr;
		if (family == AF_INET6 && requested)
			index = requested;
	}
	for (const auto &a : Addresses())
		if (!a.loopback && a.address.si_family == family && (!index || index == a.index) &&
		    (!bound || SamePrefix(source, a, family == AF_INET ? 32 : 128)) &&
		    (family != AF_INET || !requested || requested == a.address.Ipv4.sin_addr.s_addr ||
		     requested == htonl(a.index)))
			return &a;
	return nullptr;
}

int ConfigureDatagram(SOCKET s, int family);

int Destination(SOCKET socket, const sockaddr *&sa, int &length, SOCKADDR_INET &ip)
{
	if (!ReadAddress(sa, length, ip))
		return !sa || (length >= int(sizeof(ADDRESS_FAMILY)) && !IsIp(sa->sa_family)) ? 0 : WSAEFAULT;
	SOCKADDR_INET bound{};
	int boundSize = sizeof(bound);
	if (getsockname(socket, reinterpret_cast<sockaddr *>(&bound), &boundSize) == SOCKET_ERROR &&
	    WSAGetLastError() == WSAEINVAL && ConfigureDatagram(socket, ip.si_family))
		return WSAGetLastError();
	bool v4 = ip.si_family == AF_INET;
	uint32_t addr = v4 ? ntohl(ip.Ipv4.sin_addr.s_addr) : 0;
	bool multicast = v4 ? IN_MULTICAST(addr) : IN6_IS_ADDR_MULTICAST(&ip.Ipv6.sin6_addr);
	bool broadcast = v4 && addr == INADDR_BROADCAST;
	if (!multicast && !broadcast)
		return IsOnLan(ip) ? 0 : WSAENETUNREACH;
	if (multicast && !v4 && (ip.Ipv6.sin6_addr.u.Byte[1] & 15) > 2)
		return WSAENETUNREACH;
	auto *a = DiscoveryInterface(socket, ip, multicast);
	if (!a)
		return WSAENETUNREACH;
	if (broadcast)
	{
		if (a->prefix >= 31)
			return WSAENETUNREACH;
		ip.Ipv4.sin_addr.s_addr = a->address.Ipv4.sin_addr.s_addr | htonl(UINT32_MAX >> a->prefix);
		sa = reinterpret_cast<sockaddr *>(&ip);
		length = sizeof(sockaddr_in);
	}
	else
	{
		DWORD iface = v4 ? a->address.Ipv4.sin_addr.s_addr : a->index, hops = 1;
		int level = v4 ? IPPROTO_IP : IPPROTO_IPV6;
		if (setsockopt(socket, level, v4 ? IP_MULTICAST_IF : IPV6_MULTICAST_IF, reinterpret_cast<char *>(&iface),
		               sizeof(iface)) ||
		    setsockopt(socket, level, v4 ? IP_MULTICAST_TTL : IPV6_MULTICAST_HOPS, reinterpret_cast<char *>(&hops),
		               sizeof(hops)))
			return WSAGetLastError();
	}
	return 0;
}

auto realConnect = ::connect;
auto realWSAConnect = ::WSAConnect;
auto realSendTo = ::sendto;
auto realWSASendTo = ::WSASendTo;
auto realWSASendMsg = ::WSASendMsg;
auto realWSAIoctl = ::WSAIoctl;
auto realConnectByList = ::WSAConnectByList;
auto realConnectByNameW = ::WSAConnectByNameW;
auto realConnectByNameA = ::WSAConnectByNameA;
auto realBind = ::bind;
auto realSetSockOpt = ::setsockopt;
auto realAccept = ::accept;
auto realWSAAccept = ::WSAAccept;

int WSAAPI LanConnect(SOCKET s, const sockaddr *sa, int len)
{
	SOCKADDR_INET ip{};
	int error = Destination(s, sa, len, ip);
	return error ? SocketError(error) : realConnect(s, sa, len);
}
int WSAAPI LanWSAConnect(SOCKET s, const sockaddr *sa, int len, LPWSABUF caller, LPWSABUF callee, LPQOS sendQos,
                         LPQOS recvQos)
{
	SOCKADDR_INET ip{};
	int error = Destination(s, sa, len, ip);
	return error ? SocketError(error) : realWSAConnect(s, sa, len, caller, callee, sendQos, recvQos);
}
int WSAAPI LanSendTo(SOCKET s, const char *data, int count, int flags, const sockaddr *sa, int len)
{
	SOCKADDR_INET ip{};
	int error = Destination(s, sa, len, ip);
	return error ? SocketError(error) : realSendTo(s, data, count, flags, sa, len);
}
int WSAAPI LanWSASendTo(SOCKET s, LPWSABUF buffers, DWORD count, LPDWORD sent, DWORD flags, const sockaddr *sa, int len,
                        LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
	SOCKADDR_INET ip{};
	int error = Destination(s, sa, len, ip);
	return error ? SocketError(error) : realWSASendTo(s, buffers, count, sent, flags, sa, len, ov, completion);
}
bool CanUseInterface(const SOCKADDR_INET &source, NET_IFINDEX index)
{
	if (!index && IsUnspecified(source))
		return true;
	for (const auto &a : Addresses())
		if (a.address.si_family == source.si_family && (!index || index == a.index) &&
		    (IsUnspecified(source) || SamePrefix(source, a, source.si_family == AF_INET ? 32 : 128)))
			return true;
	return false;
}

int CheckSendControls(WSAMSG *msg)
{
	if (msg->Control.len && !msg->Control.buf)
		return WSAEFAULT;
	for (auto *header = WSA_CMSG_FIRSTHDR(msg); header; header = WSA_CMSG_NXTHDR(msg, header))
	{
		auto remaining = msg->Control.len - (reinterpret_cast<char *>(header) - msg->Control.buf);
		if (header->cmsg_len < WSA_CMSG_LEN(0) || header->cmsg_len > static_cast<size_t>(remaining))
			return WSAEINVAL;
		SOCKADDR_INET source{};
		NET_IFINDEX index = 0;
		if (header->cmsg_level == IPPROTO_IP && header->cmsg_type == IP_PKTINFO)
		{
			if (header->cmsg_len < WSA_CMSG_LEN(sizeof(IN_PKTINFO)))
				return WSAEINVAL;
			auto *packet = reinterpret_cast<IN_PKTINFO *>(WSA_CMSG_DATA(header));
			source.Ipv4.sin_family = AF_INET;
			source.Ipv4.sin_addr = packet->ipi_addr;
			index = packet->ipi_ifindex;
		}
		else if (header->cmsg_level == IPPROTO_IPV6 && header->cmsg_type == IPV6_PKTINFO)
		{
			if (header->cmsg_len < WSA_CMSG_LEN(sizeof(IN6_PKTINFO)))
				return WSAEINVAL;
			auto *packet = reinterpret_cast<IN6_PKTINFO *>(WSA_CMSG_DATA(header));
			source.Ipv6.sin6_family = AF_INET6;
			source.Ipv6.sin6_addr = packet->ipi6_addr;
			index = packet->ipi6_ifindex;
		}
		else
			continue;
		if (!CanUseInterface(source, index))
			return WSAENETUNREACH;
	}
	return 0;
}

int WSAAPI HookWSASendMsg(SOCKET s, LPWSAMSG msg, DWORD flags, LPDWORD sent, LPWSAOVERLAPPED ov,
                          LPWSAOVERLAPPED_COMPLETION_ROUTINE cb)
{
	if (!msg)
		return SocketError(WSAEFAULT);
	if (virtualMask)
	{
		if (msg->Control.len)
			return SocketError(WSAEOPNOTSUPP);
		return WSASendTo(s, msg->lpBuffers, msg->dwBufferCount, sent, flags, msg->name, msg->namelen, ov, cb);
	}
	SOCKADDR_INET ip{};
	WSAMSG copy = *msg;
	const sockaddr *sa = copy.name;
	int error = CheckSendControls(&copy);
	if (!error)
		error = Destination(s, sa, copy.namelen, ip);
	if (error)
		return SocketError(error);
	copy.name = const_cast<sockaddr *>(sa);
	GUID id = WSAID_WSASENDMSG;
	LPFN_WSASENDMSG sendMsg = nullptr;
	DWORD bytes;
	if (realWSAIoctl(s, SIO_GET_EXTENSION_FUNCTION_POINTER, &id, sizeof(id), &sendMsg, sizeof(sendMsg), &bytes, nullptr,
	                 nullptr))
		return SOCKET_ERROR;
	return sendMsg(s, &copy, flags, sent, ov, cb);
}

BOOL PASCAL LanConnectEx(SOCKET s, const sockaddr *sa, int len, PVOID data, DWORD count, LPDWORD sent, LPOVERLAPPED ov)
{
	SOCKADDR_INET ip{};
	int error = Destination(s, sa, len, ip);
	if (error)
	{
		WSASetLastError(error);
		return FALSE;
	}
	GUID id = WSAID_CONNECTEX;
	LPFN_CONNECTEX connectEx = nullptr;
	DWORD bytes;
	if (realWSAIoctl(s, SIO_GET_EXTENSION_FUNCTION_POINTER, &id, sizeof(id), &connectEx, sizeof(connectEx), &bytes,
	                 nullptr, nullptr))
		return FALSE;
	return connectEx(s, sa, len, data, count, sent, ov);
}

BOOL PASCAL HookConnectByList(SOCKET s, PSOCKET_ADDRESS_LIST list, LPDWORD localLen, sockaddr *local, LPDWORD remoteLen,
                              sockaddr *remote, const timeval *timeout, LPWSAOVERLAPPED reserved)
{
	if (virtualMask)
	{
		WSASetLastError(WSAEOPNOTSUPP);
		return FALSE;
	}
	if (!list || list->iAddressCount < 1)
	{
		WSASetLastError(WSAEINVAL);
		return FALSE;
	}
	for (int i = 0; i < list->iAddressCount; ++i)
	{
		SOCKADDR_INET ip{};
		auto &entry = list->Address[i];
		if (!ReadAddress(entry.lpSockaddr, entry.iSockaddrLength, ip) || !IsOnLan(ip))
		{
			WSASetLastError(WSAENETUNREACH);
			return FALSE;
		}
	}
	return realConnectByList(s, list, localLen, local, remoteLen, remote, timeout, reserved);
}

BOOL PASCAL HookConnectByNameW(SOCKET s, LPWSTR name, LPWSTR service, LPDWORD localLen, sockaddr *local,
                               LPDWORD remoteLen, sockaddr *remote, const timeval *timeout, LPWSAOVERLAPPED reserved)
{
	if (virtualMask)
	{
		WSASetLastError(WSAEOPNOTSUPP);
		return FALSE;
	}
	ADDRINFOW hints{};
	hints.ai_socktype = SOCK_STREAM;
	PADDRINFOW results = nullptr;
	int error = GetAddrInfoW(name, service, &hints, &results);
	if (error)
	{
		WSASetLastError(error);
		return FALSE;
	}
	std::vector<SOCKET_ADDRESS> addresses;
	for (auto *item = results; item; item = item->ai_next)
	{
		SOCKADDR_INET ip{};
		if (ReadAddress(item->ai_addr, static_cast<int>(item->ai_addrlen), ip) && IsOnLan(ip))
			addresses.push_back({item->ai_addr, static_cast<int>(item->ai_addrlen)});
	}
	BOOL connected = FALSE;
	error = WSAENETUNREACH;
	if (!addresses.empty())
	{
		std::vector<BYTE> buffer(offsetof(SOCKET_ADDRESS_LIST, Address) + addresses.size() * sizeof(SOCKET_ADDRESS));
		auto *list = reinterpret_cast<SOCKET_ADDRESS_LIST *>(buffer.data());
		list->iAddressCount = static_cast<int>(addresses.size());
		memcpy(list->Address, addresses.data(), addresses.size() * sizeof(SOCKET_ADDRESS));
		connected = realConnectByList(s, list, localLen, local, remoteLen, remote, timeout, reserved);
		error = WSAGetLastError();
	}
	FreeAddrInfoW(results);
	if (!connected)
		WSASetLastError(error);
	return connected;
}
BOOL PASCAL HookConnectByNameA(SOCKET s, LPCSTR name, LPCSTR service, LPDWORD localLen, sockaddr *local,
                               LPDWORD remoteLen, sockaddr *remote, const timeval *timeout, LPWSAOVERLAPPED reserved)
{
	wchar_t node[256]{}, port[256]{};
	if (!MultiByteToWideChar(CP_ACP, 0, name, -1, node, 256) || !MultiByteToWideChar(CP_ACP, 0, service, -1, port, 256))
	{
		WSASetLastError(WSAEINVAL);
		return FALSE;
	}
	return HookConnectByNameW(s, node, port, localLen, local, remoteLen, remote, timeout, reserved);
}

int WSAAPI LanWSAIoctl(SOCKET s, DWORD code, LPVOID in, DWORD inLen, LPVOID out, DWORD outLen, LPDWORD bytes,
                       LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cb)
{
	if (code == SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER)
		return SocketError(WSAEOPNOTSUPP);
	if (code == SIO_GET_EXTENSION_FUNCTION_POINTER && in && inLen == sizeof(GUID))
	{
		const GUID connectId = WSAID_CONNECTEX, sendId = WSAID_WSASENDMSG;
		auto &id = *static_cast<const GUID *>(in);
		if (id == connectId || id == sendId)
		{
			if (ov || cb)
				return SocketError(WSAEOPNOTSUPP);
			int result = realWSAIoctl(s, code, in, inLen, out, outLen, bytes, nullptr, nullptr);
			if (!result && out && outLen >= sizeof(void *))
				*static_cast<void **>(out) =
				    id == connectId ? reinterpret_cast<void *>(LanConnectEx) : reinterpret_cast<void *>(HookWSASendMsg);
			return result;
		}
	}
	return realWSAIoctl(s, code, in, inLen, out, outLen, bytes, ov, cb);
}

int RestrictDatagrams(SOCKET s, int family)
{
	int level = family == AF_INET ? IPPROTO_IP : IPPROTO_IPV6;
	int option = family == AF_INET ? IP_IFLIST : IPV6_IFLIST;
	DWORD enabled = 0;
	int size = sizeof(enabled);
	if (!getsockopt(s, level, option, reinterpret_cast<char *>(&enabled), &size) && enabled)
		return 0;
	DWORD enable = 1;
	if (setsockopt(s, level, option, reinterpret_cast<char *>(&enable), sizeof(enable)))
		return SOCKET_ERROR;
	NET_IFINDEX previous = 0;
	for (const auto &a : Addresses())
	{
		if (a.address.si_family != family || a.index == previous)
			continue;
		previous = a.index;
		DWORD index = a.index;
		if (setsockopt(s, level, family == AF_INET ? IP_ADD_IFLIST : IPV6_ADD_IFLIST, reinterpret_cast<char *>(&index),
		               sizeof(index)))
			return SOCKET_ERROR;
	}
	return 0;
}

int ConfigureDatagram(SOCKET s, int family)
{
	int type = 0, size = sizeof(type);
	if (getsockopt(s, SOL_SOCKET, SO_TYPE, reinterpret_cast<char *>(&type), &size) || type != SOCK_DGRAM)
		return 0;
	if (RestrictDatagrams(s, family))
		return SOCKET_ERROR;
	DWORD v6Only = 1;
	size = sizeof(v6Only);
	if (family == AF_INET6 && !getsockopt(s, IPPROTO_IPV6, IPV6_V6ONLY, reinterpret_cast<char *>(&v6Only), &size) &&
	    !v6Only && RestrictDatagrams(s, AF_INET))
		return SOCKET_ERROR;
	return 0;
}

int WSAAPI LanBind(SOCKET s, const sockaddr *sa, int len)
{
	SOCKADDR_INET ip{};
	if (ReadAddress(sa, len, ip))
	{
		if (!IsUnspecified(ip) && !IsOnLan(ip))
			return SocketError(WSAEADDRNOTAVAIL);
		if (ConfigureDatagram(s, ip.si_family))
			return SOCKET_ERROR;
	}
	return realBind(s, sa, len);
}

bool PeerAllowed(SOCKET s)
{
	SOCKADDR_INET peer{};
	int size = sizeof(peer);
	if (getpeername(s, reinterpret_cast<sockaddr *>(&peer), &size) || !IsIp(peer.si_family))
		return true;
	if (!IsOnLan(peer))
		return false;
	SOCKADDR_INET local{};
	size = sizeof(local);
	return getsockname(s, reinterpret_cast<sockaddr *>(&local), &size) || IsOnLan(local);
}

int WSAAPI LanSetSockOpt(SOCKET s, int level, int option, const char *value, int len)
{
	if (value && len >= int(sizeof(DWORD)) && (level == IPPROTO_IP || level == IPPROTO_IPV6) &&
	    (option == IP_UNICAST_IF || option == IP_MULTICAST_IF))
	{
		DWORD requested;
		memcpy(&requested, value, sizeof(requested));
		SOCKADDR_INET source{};
		source.si_family = level == IPPROTO_IP ? AF_INET : AF_INET6;
		NET_IFINDEX index = level == IPPROTO_IP ? ntohl(requested) : requested;
		if (level == IPPROTO_IP && option == IP_MULTICAST_IF && (index >> 24))
		{
			source.Ipv4.sin_addr.s_addr = requested;
			index = 0;
		}
		if (!CanUseInterface(source, index))
			return SocketError(WSAENETUNREACH);
	}
	int result = realSetSockOpt(s, level, option, value, len);
	if (!result && level == SOL_SOCKET && option == SO_UPDATE_ACCEPT_CONTEXT && !PeerAllowed(s))
	{
		shutdown(s, SD_BOTH);
		return SocketError(WSAECONNABORTED);
	}
	return result;
}

SOCKET CheckAccepted(SOCKET s)
{
	if (s == INVALID_SOCKET)
		return s;
	if (!PeerAllowed(s))
	{
		closesocket(s);
		SocketError(WSAECONNABORTED);
		return INVALID_SOCKET;
	}
	return s;
}
SOCKET WSAAPI LanAccept(SOCKET s, sockaddr *sa, int *len)
{
	return CheckAccepted(realAccept(s, sa, len));
}
SOCKET WSAAPI LanWSAAccept(SOCKET s, sockaddr *sa, int *len, LPCONDITIONPROC fn, DWORD_PTR data)
{
	return CheckAccepted(realWSAAccept(s, sa, len, fn, data));
}
}

// Local DNS cache lookups never send a query. A single-label or .local peer may
// also be discovered by multicast on a LAN interface.
bool ResolveLanName(const wchar_t *name, int family, wchar_t (&text)[64], bool allowMulticast)
{
	const auto &interfaces = Addresses();
	for (const auto &a : interfaces)
	{
		if (a.loopback || (family != AF_UNSPEC && family != a.address.si_family))
			continue;
		DNS_QUERY_REQUEST request{};
		request.Version = DNS_QUERY_REQUEST_VERSION1;
		request.QueryName = name;
		request.QueryType = a.address.si_family == AF_INET ? DNS_TYPE_A : DNS_TYPE_AAAA;
		request.QueryOptions = allowMulticast ? DNS_QUERY_MULTICAST_ONLY : DNS_QUERY_NO_WIRE_QUERY;
		request.InterfaceIndex = a.index;
		DNS_QUERY_RESULT result{};
		result.Version = DNS_QUERY_RESULTS_VERSION1;
		DnsQueryEx(&request, &result, nullptr);
		bool found = false;
		for (auto *r = result.pQueryRecords; r; r = r->pNext)
		{
			SOCKADDR_INET ip{};
			ip.si_family = a.address.si_family;
			if (r->wType != request.QueryType)
				continue;
			if (ip.si_family == AF_INET)
				ip.Ipv4.sin_addr.s_addr = r->Data.A.IpAddress;
			else
			{
				memcpy(&ip.Ipv6.sin6_addr, &r->Data.AAAA.Ip6Address, sizeof(in6_addr));
				if (IN6_IS_ADDR_LINKLOCAL(&ip.Ipv6.sin6_addr))
					ip.Ipv6.sin6_scope_id = a.index;
			}
			DWORD size = 64;
			if (IsOnLan(ip) &&
			    !WSAAddressToStringW(reinterpret_cast<sockaddr *>(&ip),
			                         ip.si_family == AF_INET ? sizeof(sockaddr_in) : sizeof(sockaddr_in6), nullptr,
			                         text, &size))
			{
				found = true;
				break;
			}
		}
		if (result.pQueryRecords)
			DnsRecordListFree(result.pQueryRecords, DnsFreeRecordList);
		if (found)
			return true;
	}
	return false;
}

LONG AttachConnectionHelpers()
{
	LONG error = NO_ERROR;
#define HOOK(name)                                                                                                     \
	if (!error)                                                                                                        \
	error = Attach(real##name, Hook##name)
	HOOK(ConnectByList);
	HOOK(ConnectByNameA);
	HOOK(ConnectByNameW);
	HOOK(WSASendMsg);
#undef HOOK
	return error;
}

LONG AttachLanHooks()
{
	LONG error = AttachNameHooks();
#define LAN(name)                                                                                                      \
	if (!error)                                                                                                        \
	error = Attach(real##name, Lan##name)
	LAN(Connect);
	LAN(WSAConnect);
	LAN(SendTo);
	LAN(WSASendTo);
	LAN(WSAIoctl);
	LAN(Bind);
	LAN(SetSockOpt);
	LAN(Accept);
	LAN(WSAAccept);
#undef LAN
	return error;
}
