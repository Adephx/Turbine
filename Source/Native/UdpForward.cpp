#include "UdpSocket.h"
#include <atomic>
#include <cstddef>
#include <limits>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace
{
auto realGetSockOpt = ::getsockopt;
auto realShutdown = ::shutdown;
auto realSelect = ::select;
auto realWSAPoll = ::WSAPoll;
auto realWSAEnumNetworkEvents = ::WSAEnumNetworkEvents;

int WSAAPI ForwardGetSockOpt(SOCKET socket, int level, int option, char *value, int *length)
{
	uint32_t adapter = 0;
	if (level == IPPROTO_IP && option == IP_MULTICAST_IF && GetVirtualMulticastInterface(socket, adapter))
	{
		if (!value || !length)
			return SocketError(WSAEFAULT);
		if (*length < static_cast<int>(sizeof(adapter)))
		{
			*length = 0;
			return SocketError(WSAEFAULT);
		}
		memcpy(value, &adapter, sizeof(adapter));
		*length = sizeof(adapter);
		return 0;
	}
	const SOCKET backing = UdpBackingSocket(socket);
	int result = realGetSockOpt(backing, level, option, value, length);
	if (!result && backing != socket && level == SOL_SOCKET &&
	    (option == SO_PROTOCOL_INFOA || option == SO_PROTOCOL_INFOW) &&
	    value && length && *length >= static_cast<int>(sizeof(DWORD)))
	{
		DWORD serviceFlags;
		memcpy(&serviceFlags, value, sizeof(serviceFlags));
		serviceFlags &= ~static_cast<DWORD>(XP1_IFS_HANDLES);
		memcpy(value, &serviceFlags, sizeof(serviceFlags));
	}
	return result;
}

int WSAAPI ForwardShutdown(SOCKET socket, int how)
{
	return realShutdown(UdpBackingSocket(socket), how);
}

int WSAAPI ForwardWSAEnumNetworkEvents(SOCKET socket, WSAEVENT event, LPWSANETWORKEVENTS events)
{
	return realWSAEnumNetworkEvents(UdpBackingSocket(socket), event, events);
}

bool CopySocketData(void *output, const void *input, size_t size)
{
	__try
	{
		memcpy(output, input, size);
		return true;
	}
	__except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION || GetExceptionCode() == EXCEPTION_IN_PAGE_ERROR
	              ? EXCEPTION_EXECUTE_HANDLER
	              : EXCEPTION_CONTINUE_SEARCH)
	{
		return false;
	}
}

struct ReadySet
{
	explicit ReadySet(fd_set *set) : caller(set) {}

	fd_set *caller;
	std::vector<SOCKET> native;
	std::unordered_map<SOCKET, SOCKET> aliases;

	bool Prepare()
	{
		if (!caller)
			return true;
		// Winsock callers can compile with an FD_SETSIZE larger than ours.
		static_assert(offsetof(fd_set, fd_array) == sizeof(SOCKET));
		u_int count;
		if (!CopySocketData(&count, caller, sizeof(count)) || count >= std::numeric_limits<size_t>::max() / sizeof(SOCKET))
			return false;
		const auto *sockets = reinterpret_cast<const BYTE *>(caller) + offsetof(fd_set, fd_array);
		for (size_t i = 0; i < count; ++i)
		{
			SOCKET socket;
			if (!CopySocketData(&socket, sockets + i * sizeof(SOCKET), sizeof(socket)))
				return false;
			SOCKET backing = UdpBackingSocket(socket);
			if (socket == backing)
				continue;
			if (native.empty())
			{
				native.resize(static_cast<size_t>(count) + 1);
				if (!CopySocketData(native.data(), caller, native.size() * sizeof(SOCKET)))
					return false;
			}
			native[i + 1] = backing;
			aliases.try_emplace(backing, socket);
		}
		return true;
	}

	fd_set *NativeSet()
	{
		return native.empty() ? caller : reinterpret_cast<fd_set *>(native.data());
	}

	bool Publish()
	{
		if (native.empty())
			return true;
		u_int count;
		memcpy(&count, native.data(), sizeof(count));
		if (count >= native.size())
			return false;
		for (size_t i = 1; i <= count; ++i)
		{
			auto alias = aliases.find(native[i]);
			if (alias != aliases.end())
				native[i] = alias->second;
		}
		return CopySocketData(caller, native.data(), (static_cast<size_t>(count) + 1) * sizeof(SOCKET));
	}
};

int WSAAPI ForwardSelect(int ignored, fd_set *read, fd_set *write, fd_set *except, const timeval *timeout)
{
	try
	{
		ReadySet readable(read);
		ReadySet writable(write != read ? write : nullptr);
		ReadySet exceptional(except != read && except != write ? except : nullptr);
		if (!readable.Prepare() || !writable.Prepare() || !exceptional.Prepare())
			return SocketError(WSAEFAULT);
		fd_set *nativeRead = readable.NativeSet();
		fd_set *nativeWrite = write == read ? nativeRead : writable.NativeSet();
		fd_set *nativeExcept = except == read ? nativeRead : except == write ? nativeWrite : exceptional.NativeSet();
		int result = realSelect(ignored, nativeRead, nativeWrite, nativeExcept, timeout);
		if (result != SOCKET_ERROR && (!readable.Publish() || !writable.Publish() || !exceptional.Publish()))
			return SocketError(WSAEFAULT);
		return result;
	}
	catch (...)
	{
		return SocketError(WSAENOBUFS);
	}
}

int WSAAPI ForwardWSAPoll(LPWSAPOLLFD descriptors, ULONG count, INT timeout)
{
	try
	{
		if (!descriptors || count > std::numeric_limits<size_t>::max() / sizeof(WSAPOLLFD))
			return realWSAPoll(descriptors, count, timeout);
		std::vector<WSAPOLLFD> native;
		for (ULONG i = 0; i < count; ++i)
		{
			SOCKET socket;
			if (!CopySocketData(&socket, &descriptors[i].fd, sizeof(socket)))
				return SocketError(WSAEFAULT);
			SOCKET backing = UdpBackingSocket(socket);
			if (socket == backing)
				continue;
			if (native.empty())
			{
				native.resize(count);
				if (!CopySocketData(native.data(), descriptors, native.size() * sizeof(WSAPOLLFD)))
					return SocketError(WSAEFAULT);
			}
			native[i].fd = backing;
		}
		if (native.empty())
			return realWSAPoll(descriptors, count, timeout);
		int result = realWSAPoll(native.data(), count, timeout);
		for (ULONG i = 0; i < count; ++i)
		{
			if (!CopySocketData(&descriptors[i].revents, &native[i].revents, sizeof(native[i].revents)))
				return SocketError(WSAEFAULT);
		}
		return result;
	}
	catch (...)
	{
		return SocketError(WSAENOBUFS);
	}
}

constexpr wchar_t RelayClass[] = L"Turbine.UdpAsyncSelectRelay.4a75f0b5";
constexpr UINT RelayNotification = WM_APP + 1;

struct AsyncRegistration
{
	SOCKET socket;
	SOCKET backing;
	HWND target;
	UINT message;
	HWND relay = nullptr;
};

enum class RelayAction { Create, Destroy, Stop };

struct RelayCommand
{
	RelayAction action;
	AsyncRegistration *registration = nullptr;
};

std::mutex asyncMutex;
std::unordered_map<SOCKET, std::unique_ptr<AsyncRegistration>> asyncRegistrations;
std::atomic<RelayCommand *> relayCommand{nullptr};
HANDLE relayThread = nullptr;
HANDLE relayReady = nullptr;
HANDLE relayDone = nullptr;
bool relayStarted = false;

LRESULT CALLBACK RelayWindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
{
	if (message == WM_NCCREATE)
	{
		const auto *creation = reinterpret_cast<const CREATESTRUCTW *>(lparam);
		SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(creation->lpCreateParams));
	}
	else if (message == RelayNotification)
	{
		auto *registration = reinterpret_cast<const AsyncRegistration *>(GetWindowLongPtrW(window, GWLP_USERDATA));
		if (registration && wparam == static_cast<WPARAM>(registration->backing))
			PostMessageW(registration->target, registration->message, static_cast<WPARAM>(registration->socket), lparam);
		return 0;
	}
	return DefWindowProcW(window, message, wparam, lparam);
}

DWORD WINAPI RunRelayThread(void *)
{
	const HINSTANCE instance = GetModuleHandleW(nullptr);
	WNDCLASSW windowClass{};
	windowClass.hInstance = instance;
	windowClass.lpfnWndProc = RelayWindowProc;
	windowClass.lpszClassName = RelayClass;
	const ATOM registered = RegisterClassW(&windowClass);
	relayStarted = registered != 0;
	SetEvent(relayDone);
	if (!registered)
		return 0;
	for (;;)
	{
		const DWORD result = MsgWaitForMultipleObjectsEx(1, &relayReady, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
		if (result == WAIT_OBJECT_0)
		{
			auto *command = relayCommand.exchange(nullptr, std::memory_order_acquire);
			if (!command)
				continue;
			if (command->action == RelayAction::Stop)
			{
				SetEvent(relayDone);
				break;
			}
			auto &registration = *command->registration;
			if (command->action == RelayAction::Create)
				registration.relay = CreateWindowExW(0, RelayClass, nullptr, 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr,
				                                     instance, &registration);
			else
			{
				SetWindowLongPtrW(registration.relay, GWLP_USERDATA, 0);
				DestroyWindow(registration.relay);
			}
			SetEvent(relayDone);
		}
		else if (result == WAIT_OBJECT_0 + 1)
		{
			MSG message;
			if (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
			{
				if (message.message == WM_QUIT)
					break;
				DispatchMessageW(&message);
			}
		}
		else
			break;
	}
	UnregisterClassW(RelayClass, instance);
	return 0;
}

bool WaitForRelay()
{
	const HANDLE events[] = {relayDone, relayThread};
	return WaitForMultipleObjects(2, events, FALSE, INFINITE) == WAIT_OBJECT_0;
}

void ReleaseRelayHandles()
{
	if (relayThread)
		CloseHandle(relayThread);
	if (relayReady)
		CloseHandle(relayReady);
	if (relayDone)
		CloseHandle(relayDone);
	relayThread = nullptr;
	relayReady = nullptr;
	relayDone = nullptr;
}

bool StartRelay()
{
	if (relayThread)
	{
		if (WaitForSingleObject(relayThread, 0) == WAIT_TIMEOUT)
			return true;
		asyncRegistrations.clear();
		ReleaseRelayHandles();
	}
	relayStarted = false;
	relayReady = CreateEventW(nullptr, FALSE, FALSE, nullptr);
	relayDone = CreateEventW(nullptr, FALSE, FALSE, nullptr);
	if (relayReady && relayDone)
		relayThread = CreateThread(nullptr, 0, RunRelayThread, nullptr, 0, nullptr);
	if (relayThread)
	{
		if (WaitForRelay() && relayStarted)
			return true;
		WaitForSingleObject(relayThread, INFINITE);
	}
	ReleaseRelayHandles();
	return false;
}

bool SendRelayCommand(RelayAction action, AsyncRegistration *registration = nullptr)
{
	RelayCommand command{action, registration};
	relayCommand.store(&command, std::memory_order_release);
	if (SetEvent(relayReady) && WaitForRelay())
		return true;
	relayCommand.store(nullptr, std::memory_order_relaxed);
	return false;
}

void ForgetRelay(SOCKET socket, SOCKET backing)
{
	auto found = asyncRegistrations.find(socket);
	if (found == asyncRegistrations.end() || !found->second || found->second->backing != backing)
		return;
	SendRelayCommand(RelayAction::Destroy, found->second.get());
	asyncRegistrations.erase(found);
}
}

void ForgetUdpAsyncSelect(SOCKET socket, SOCKET backing)
{
	std::lock_guard lock(asyncMutex);
	ForgetRelay(socket, backing);
}

void ClearUdpAsyncSelect()
{
	std::lock_guard lock(asyncMutex);
	if (!relayThread)
		return;
	for (const auto &entry : asyncRegistrations)
		SendRelayCommand(RelayAction::Destroy, entry.second.get());
	SendRelayCommand(RelayAction::Stop);
	WaitForSingleObject(relayThread, INFINITE);
	asyncRegistrations.clear();
	ReleaseRelayHandles();
}

int UdpAsyncSelect(SOCKET socket, HWND window, unsigned int message, long events, decltype(&::WSAAsyncSelect) native)
{
	const SOCKET backing = UdpBackingSocket(socket);
	if (backing == socket)
		return native(socket, window, message, events);
	std::lock_guard lock(asyncMutex);
	if (!events)
	{
		const int result = native(backing, window, message, 0);
		const int error = WSAGetLastError();
		if (!result)
			ForgetRelay(socket, backing);
		WSASetLastError(error);
		return result;
	}
	if (!IsWindow(window))
		return SocketError(WSAEINVAL);
	try
	{
		if (!StartRelay())
			return SocketError(WSAENOBUFS);
		auto registration = std::make_unique<AsyncRegistration>(AsyncRegistration{socket, backing, window, message});
		auto [slot, inserted] = asyncRegistrations.try_emplace(socket);
		if (!SendRelayCommand(RelayAction::Create, registration.get()) || !registration->relay)
		{
			if (inserted)
				asyncRegistrations.erase(slot);
			return SocketError(WSAENOBUFS);
		}
		const int result = native(backing, registration->relay, RelayNotification, events);
		const int error = WSAGetLastError();
		if (!result)
		{
			if (slot->second)
				SendRelayCommand(RelayAction::Destroy, slot->second.get());
			slot->second = std::move(registration);
		}
		else
		{
			SendRelayCommand(RelayAction::Destroy, registration.get());
			if (inserted)
				asyncRegistrations.erase(slot);
		}
		WSASetLastError(error);
		return result;
	}
	catch (...)
	{
		return SocketError(WSAENOBUFS);
	}
}

LONG AttachUdpForwardHooks()
{
	LONG error = Attach(realGetSockOpt, ForwardGetSockOpt);
	if (!error)
		error = Attach(realShutdown, ForwardShutdown);
	if (!error)
		error = Attach(realSelect, ForwardSelect);
	if (!error)
		error = Attach(realWSAPoll, ForwardWSAPoll);
	if (!error)
		error = Attach(realWSAEnumNetworkEvents, ForwardWSAEnumNetworkEvents);
	return error;
}

