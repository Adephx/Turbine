#include "UdpSocket.h"
#include "UdpCallback.h"
#include <winternl.h>
#include <atomic>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace
{
using SetFileInformation = LONG(NTAPI *)(HANDLE, void *, void *, ULONG, ULONG);
using SetIoCompletion = LONG(NTAPI *)(HANDLE, void *, void *, LONG, ULONG_PTR);
using DuplicateObject = LONG(NTAPI *)(HANDLE, HANDLE, HANDLE, HANDLE *, ACCESS_MASK, ULONG, ULONG);
using CompareObjects = LONG(NTAPI *)(HANDLE, HANDLE);
using ResetEventState = LONG(NTAPI *)(HANDLE, LONG *);
SetFileInformation realSetFileInformation = nullptr;
SetIoCompletion setIoCompletion = nullptr;
DuplicateObject realDuplicateObject = nullptr;
CompareObjects compareObjects = nullptr;
ResetEventState resetEventState = nullptr;
decltype(&::NtQueryObject) queryObject = nullptr;
decltype(&::NtClose) realCloseObject = nullptr;
auto realCancelIoEx = ::CancelIoEx;
auto realCancelIo = ::CancelIo;
auto realWsaResult = ::WSAGetOverlappedResult;
auto realReadFile = ::ReadFile;
auto realWriteFile = ::WriteFile;
auto realReadFileEx = ::ReadFileEx;
auto realWriteFileEx = ::WriteFileEx;
auto realGetFileType = ::GetFileType;
auto realRecv = ::recv;
auto realWSARecv = ::WSARecv;
decltype(&::RtlNtStatusToDosError) statusToError = nullptr;
constexpr LONG Pending = 0x103;
constexpr LONG BufferOverflow = static_cast<LONG>(0x80000005u);
bool IsPublicUdp(HANDLE handle);

struct CompletionPort
{
	HANDLE original = nullptr;
	HANDLE copy = nullptr;
	~CompletionPort()
	{
		if (copy)
			realCloseObject(copy);
	}
};

std::mutex portMutex;
std::vector<std::weak_ptr<CompletionPort>> ports;
std::atomic<bool> havePorts{false};

std::shared_ptr<CompletionPort> ReferencePort(HANDLE handle)
{
	std::lock_guard<std::mutex> lock(portMutex);
	for (auto entry = ports.begin(); entry != ports.end();)
	{
		auto port = entry->lock();
		if (!port)
			entry = ports.erase(entry);
		else
		{
			if (port->copy && compareObjects(port->copy, handle) == 0)
				return port;
			++entry;
		}
	}
	auto port = std::make_shared<CompletionPort>();
	if (realDuplicateObject(GetCurrentProcess(), handle, GetCurrentProcess(), &port->copy, 0, 0,
	                        DUPLICATE_SAME_ACCESS) != 0)
		return {};
	port->original = handle;
	ports.push_back(port);
	havePorts.store(true, std::memory_order_release);
	return port;
}

// An extra user handle would keep a native completion-port waiter blocked after the game closes its last handle.
template <class Close> LONG ClosePortHandle(HANDLE handle, Close close)
{
	if (!havePorts.load(std::memory_order_acquire))
		return close();
	std::unique_lock<std::mutex> lock(portMutex);
	for (auto entry = ports.begin(); entry != ports.end();)
	{
		auto port = entry->lock();
		if (!port || !port->copy)
		{
			entry = ports.erase(entry);
			continue;
		}
		if (port->original == handle || (!port->original && compareObjects(port->copy, handle) == 0))
		{
			LONG result = close();
			if (port->original == handle && compareObjects(port->copy, handle) != 0)
				port->original = nullptr;
			PUBLIC_OBJECT_BASIC_INFORMATION info{};
			if (!port->original && queryObject(port->copy, ObjectBasicInformation, &info, sizeof(info), nullptr) == 0 &&
			    info.HandleCount == 1)
			{
				realCloseObject(port->copy);
				port->copy = nullptr;
			}
			return result;
		}
		++entry;
	}
	if (ports.empty())
		havePorts.store(false, std::memory_order_release);
	lock.unlock();
	return close();
}

LONG NTAPI HookCloseObject(HANDLE handle)
{
	if (IsPublicUdp(handle))
		return CloseGameUdp(reinterpret_cast<SOCKET>(handle)) == 0 ? 0 : static_cast<LONG>(0xc0000008u);
	return ClosePortHandle(handle, [&] { return realCloseObject(handle); });
}

LONG NTAPI HookDuplicateObject(HANDLE process, HANDLE handle, HANDLE targetProcess, HANDLE *target, ACCESS_MASK access,
                               ULONG attributes, ULONG options)
{
	auto duplicate = [&] {
		return realDuplicateObject(process, handle, targetProcess, target, access, attributes, options);
	};
	if ((options & DUPLICATE_CLOSE_SOURCE) &&
	    (process == GetCurrentProcess() || compareObjects(GetCurrentProcess(), process) == 0))
	{
		if (IsPublicUdp(handle))
		{
			LONG result = realDuplicateObject(process, handle, targetProcess, target, access, attributes,
			                                  options & ~DUPLICATE_CLOSE_SOURCE);
			CloseGameUdp(reinterpret_cast<SOCKET>(handle));
			return result;
		}
		return ClosePortHandle(handle, duplicate);
	}
	return duplicate();
}

struct UdpHandle
{
	uint64_t identity = 0;
	SOCKET backing = INVALID_SOCKET;
	// The game waits on this event so backing I/O cannot wake it before sender translation.
	HANDLE visible = nullptr;
	HANDLE publicHandle = nullptr;
	std::shared_ptr<CompletionPort> port;
	void *key = nullptr;
	bool skipSuccess = false;
	bool skipFileEvent = false;
	bool closing = false;
	size_t outstanding = 0;
	uint64_t signalEpoch = 0;
	~UdpHandle()
	{
		if (publicHandle && compareObjects(visible, publicHandle) == 0)
			realCloseObject(publicHandle);
		if (visible)
			realCloseObject(visible);
	}
};
std::mutex handlesMutex;
std::unordered_map<SOCKET, std::shared_ptr<UdpHandle>> handles;
std::atomic<bool> haveHandles{false};
uint64_t nextIdentity = 0;

std::shared_ptr<UdpHandle> FindUdp(SOCKET value)
{
	if (!haveHandles.load(std::memory_order_acquire))
		return {};
	std::lock_guard<std::mutex> lock(handlesMutex);
	auto found = handles.find(value);
	if (found == handles.end() || compareObjects(found->second->visible, reinterpret_cast<HANDLE>(value)) != 0)
		return {};
	return found->second;
}

bool IsPublicUdp(HANDLE handle)
{
	return static_cast<bool>(FindUdp(reinterpret_cast<SOCKET>(handle)));
}

struct Operation
{
	WSAOVERLAPPED kernel{};
	WSAOVERLAPPED *user = nullptr;
	std::shared_ptr<UdpHandle> socket;
	std::shared_ptr<UdpCallback> callback;
	sockaddr *source = nullptr;
	int *sourceLength = nullptr;
	HANDLE kernelEvent = nullptr;
	HANDLE userEvent = nullptr;
	PTP_WAIT wait = nullptr;
	bool allowPort = true;
	bool fileCallback = false;
	bool fileIo = false;
	uint64_t signalEpoch = 0;
	~Operation()
	{
		if (wait)
			CloseThreadpoolWait(wait);
		if (kernelEvent)
			realCloseObject(kernelEvent);
		if (userEvent)
			realCloseObject(userEvent);
	}
};

std::mutex ioMutex;
std::condition_variable finished;
std::unordered_map<WSAOVERLAPPED *, std::unique_ptr<Operation>> operations;

LONG NTAPI HookSetFileInformation(HANDLE file, void *status, void *info, ULONG size, ULONG kind)
{
	constexpr ULONG AssociatePort = 30, SetModes = 41, ReplacePort = 61;
	if (kind != AssociatePort && kind != ReplacePort && kind != SetModes)
		return realSetFileInformation(file, status, info, size, kind);
	auto socket = FindUdp(reinterpret_cast<SOCKET>(file));
	if (!socket)
		return realSetFileInformation(file, status, info, size, kind);
	struct Binding
	{
		HANDLE port;
		void *key;
	};
	Binding binding{};
	std::shared_ptr<CompletionPort> port;
	if (kind != SetModes)
	{
		if (size < sizeof(binding) || !ReadProcessMemory(GetCurrentProcess(), info, &binding, sizeof(binding), nullptr))
			return realSetFileInformation(reinterpret_cast<HANDLE>(socket->backing), status, info, size, kind);
		if (binding.port)
		{
			try
			{
				port = ReferencePort(binding.port);
			}
			catch (...)
			{
			}
			if (!port)
				return static_cast<LONG>(0xc000009au); // STATUS_INSUFFICIENT_RESOURCES
		}
	}
	std::lock_guard<std::mutex> lock(ioMutex);
	LONG result = realSetFileInformation(reinterpret_cast<HANDLE>(socket->backing), status, info, size, kind);
	if (result != 0)
		return result;
	if (kind == SetModes)
	{
		ULONG modes = *static_cast<const ULONG *>(info);
		socket->skipSuccess |= (modes & FILE_SKIP_COMPLETION_PORT_ON_SUCCESS) != 0;
		socket->skipFileEvent |= (modes & FILE_SKIP_SET_EVENT_ON_HANDLE) != 0;
		if (modes & FILE_SKIP_SET_EVENT_ON_HANDLE)
		{
			++socket->signalEpoch;
			ResetEvent(socket->visible);
		}
		return result;
	}
	socket->key = binding.key;
	socket->port = std::move(port);
	return result;
}

void Publish(Operation &op, bool immediate, bool notify, bool hasSource, bool restoreEvent = false,
             bool restoreFile = false)
{
	LONG status = static_cast<LONG>(op.kernel.Internal);
	std::shared_ptr<CompletionPort> port;
	void *key = nullptr;
	bool signalSocket = false;
	{
		std::lock_guard<std::mutex> lock(ioMutex);
		if (op.allowPort && !op.callback && !(immediate && op.socket->skipSuccess))
		{
			port = op.socket->port;
			key = op.socket->key;
		}
		signalSocket = !op.socket->skipFileEvent && !op.userEvent && (!immediate || op.callback || op.fileIo);
	}
	if (hasSource && op.source)
		TranslateUdpSource(op.source, op.sourceLength);
	auto callback = op.callback;
	DWORD flags = 0, transferred = 0;
	DWORD error = 0;
	if (callback && notify)
	{
		if (op.fileCallback)
			error = status == BufferOverflow ? ERROR_SUCCESS : statusToError(status);
		else if (!realWsaResult(op.socket->backing, &op.kernel, &transferred, FALSE, &flags))
			error = WSAGetLastError();
	}
	auto *user = op.user;
	DWORD bytes = static_cast<DWORD>(op.kernel.InternalHigh);
	{
		std::lock_guard<std::mutex> lock(ioMutex);
		user->Offset = op.kernel.Offset;
		user->OffsetHigh = op.kernel.OffsetHigh;
		user->InternalHigh = op.kernel.InternalHigh;
		InterlockedExchangePointer(reinterpret_cast<void *volatile *>(&user->Internal),
		                           reinterpret_cast<void *>(op.kernel.Internal));
		if ((notify || restoreEvent) && op.userEvent)
			SetEvent(op.userEvent);
		if (notify && port)
		{
			// Non-IFS status is already public when an IOCP consumer receives this packet.
			std::lock_guard<std::mutex> portLock(portMutex);
			if (port->copy)
				setIoCompletion(port->copy, key, user, status, bytes);
		}
		if ((notify && signalSocket) || (restoreFile && op.signalEpoch == op.socket->signalEpoch))
			SetEvent(op.socket->visible);
		if (notify && callback)
			QueueUdpCallback(callback, error, bytes, user, flags);
		--op.socket->outstanding;
		operations.erase(user);
	}
	finished.notify_all();
}

void CALLBACK Completed(PTP_CALLBACK_INSTANCE, void *context, PTP_WAIT, TP_WAIT_RESULT)
{
	auto &operation = *static_cast<Operation *>(context);
	LONG status = static_cast<LONG>(operation.kernel.Internal);
	Publish(operation, false, true, status >= 0 || status == BufferOverflow);
}

BOOL WINAPI HookCancelIoEx(HANDLE file, LPOVERLAPPED overlap)
{
	auto socket = FindUdp(reinterpret_cast<SOCKET>(file));
	if (!socket)
		return realCancelIoEx(file, overlap);
	std::lock_guard<std::mutex> lock(ioMutex);
	auto found = operations.find(overlap);
	if (found != operations.end() && found->second->socket == socket)
		overlap = &found->second->kernel;
	return realCancelIoEx(reinterpret_cast<HANDLE>(socket->backing), overlap);
}

BOOL WINAPI HookCancelIo(HANDLE file)
{
	return realCancelIo(reinterpret_cast<HANDLE>(UdpBackingSocket(reinterpret_cast<SOCKET>(file))));
}

BOOL WSAAPI HookWsaResult(SOCKET game, LPWSAOVERLAPPED overlap, LPDWORD transferred, BOOL wait, LPDWORD flags)
{
	auto socket = FindUdp(game);
	if (!socket)
		return realWsaResult(game, overlap, transferred, wait, flags);
	if (wait && overlap && !HasOverlappedIoCompleted(overlap))
	{
		HANDLE event = overlap->hEvent ? overlap->hEvent : socket->visible;
		if (WaitForSingleObject(event, INFINITE) == WAIT_FAILED)
			return FALSE;
	}
	return realWsaResult(socket->backing, overlap, transferred, FALSE, flags);
}

int WSAAPI HookRecv(SOCKET game, char *buffer, int size, int flags)
{
	return realRecv(UdpBackingSocket(game), buffer, size, flags);
}

int WSAAPI HookWSARecv(SOCKET game, LPWSABUF buffers, DWORD count, LPDWORD bytes, LPDWORD flags,
                       LPWSAOVERLAPPED overlap, LPWSAOVERLAPPED_COMPLETION_ROUTINE callback)
{
	SOCKET backing = UdpBackingSocket(game);
	if (backing == game || !overlap)
		return realWSARecv(backing, buffers, count, bytes, flags, overlap, callback);
	return SubmitUdpIo(game, overlap, callback, nullptr, nullptr, [&](SOCKET native, LPWSAOVERLAPPED op) {
		return realWSARecv(native, buffers, count, bytes, flags, op, nullptr);
	});
}

DWORD WINAPI HookGetFileType(HANDLE file)
{
	return realGetFileType(reinterpret_cast<HANDLE>(UdpBackingSocket(reinterpret_cast<SOCKET>(file))));
}

BOOL WINAPI HookReadFile(HANDLE file, void *buffer, DWORD size, DWORD *bytes, OVERLAPPED *overlap)
{
	SOCKET game = reinterpret_cast<SOCKET>(file), backing = UdpBackingSocket(game);
	if (game == backing || !overlap)
		return realReadFile(reinterpret_cast<HANDLE>(backing), buffer, size, bytes, overlap);
	return !SubmitUdpIo(
	    game, overlap, nullptr, nullptr, nullptr,
	    [&](SOCKET native, LPWSAOVERLAPPED op) {
		    return realReadFile(reinterpret_cast<HANDLE>(native), buffer, size, bytes, op) ? 0 : SOCKET_ERROR;
	    },
	    nullptr, true);
}

BOOL WINAPI HookWriteFile(HANDLE file, const void *buffer, DWORD size, DWORD *bytes, OVERLAPPED *overlap)
{
	SOCKET game = reinterpret_cast<SOCKET>(file), backing = UdpBackingSocket(game);
	if (game == backing || !overlap)
		return realWriteFile(reinterpret_cast<HANDLE>(backing), buffer, size, bytes, overlap);
	return !SubmitUdpIo(
	    game, overlap, nullptr, nullptr, nullptr,
	    [&](SOCKET native, LPWSAOVERLAPPED op) {
		    return realWriteFile(reinterpret_cast<HANDLE>(native), buffer, size, bytes, op) ? 0 : SOCKET_ERROR;
	    },
	    nullptr, true);
}

BOOL WINAPI HookReadFileEx(HANDLE file, void *buffer, DWORD size, OVERLAPPED *overlap,
                           LPOVERLAPPED_COMPLETION_ROUTINE cb)
{
	SOCKET game = reinterpret_cast<SOCKET>(file), backing = UdpBackingSocket(game);
	if (game == backing || !overlap || !cb)
		return realReadFileEx(reinterpret_cast<HANDLE>(backing), buffer, size, overlap, cb);
	DWORD entryError = GetLastError();
	int result = SubmitUdpIo(
	    game, overlap, nullptr, nullptr, nullptr,
	    [&](SOCKET native, LPWSAOVERLAPPED op) {
		    return realReadFile(reinterpret_cast<HANDLE>(native), buffer, size, nullptr, op) ? 0 : SOCKET_ERROR;
	    },
	    cb, true);
	if (result && GetLastError() != ERROR_IO_PENDING)
		return FALSE;
	SetLastError(entryError);
	return TRUE;
}

BOOL WINAPI HookWriteFileEx(HANDLE file, const void *buffer, DWORD size, OVERLAPPED *overlap,
                            LPOVERLAPPED_COMPLETION_ROUTINE cb)
{
	SOCKET game = reinterpret_cast<SOCKET>(file), backing = UdpBackingSocket(game);
	if (game == backing || !overlap || !cb)
		return realWriteFileEx(reinterpret_cast<HANDLE>(backing), buffer, size, overlap, cb);
	DWORD entryError = GetLastError();
	int result = SubmitUdpIo(
	    game, overlap, nullptr, nullptr, nullptr,
	    [&](SOCKET native, LPWSAOVERLAPPED op) {
		    return realWriteFile(reinterpret_cast<HANDLE>(native), buffer, size, nullptr, op) ? 0 : SOCKET_ERROR;
	    },
	    cb, true);
	if (result && GetLastError() != ERROR_IO_PENDING)
		return FALSE;
	SetLastError(entryError);
	return TRUE;
}
}

SOCKET MakeUdpSocket(SOCKET backing)
{
	try
	{
		auto socket = std::make_shared<UdpHandle>();
		socket->backing = backing;
		if (!SetHandleInformation(reinterpret_cast<HANDLE>(backing), HANDLE_FLAG_INHERIT, 0))
			return INVALID_SOCKET;
		socket->visible = CreateEventW(nullptr, TRUE, TRUE, nullptr);
		if (!socket->visible)
			return INVALID_SOCKET;
		if (realDuplicateObject(GetCurrentProcess(), socket->visible, GetCurrentProcess(), &socket->publicHandle, 0, 0,
		                        DUPLICATE_SAME_ACCESS) != 0)
			return INVALID_SOCKET;
		SOCKET game = reinterpret_cast<SOCKET>(socket->publicHandle);
		std::lock_guard<std::mutex> lock(handlesMutex);
		socket->identity = ++nextIdentity;
		if (!handles.emplace(game, std::move(socket)).second)
			return INVALID_SOCKET;
		haveHandles.store(true, std::memory_order_release);
		return game;
	}
	catch (...)
	{
		return INVALID_SOCKET;
	}
}

SOCKET UdpBackingSocket(SOCKET game)
{
	auto socket = FindUdp(game);
	return socket ? socket->backing : game;
}

void ResetUdpSocketSignal(SOCKET game)
{
	auto socket = FindUdp(game);
	if (socket)
	{
		std::lock_guard<std::mutex> lock(ioMutex);
		++socket->signalEpoch;
		ResetEvent(socket->visible);
	}
}

void CancelUdpSocket(SOCKET game)
{
	auto socket = FindUdp(game);
	if (!socket)
		return;
	std::unique_lock<std::mutex> lock(ioMutex);
	socket->closing = true;
	realCancelIoEx(reinterpret_cast<HANDLE>(socket->backing), nullptr);
	finished.wait(lock, [&] { return !socket->outstanding; });
}

void ReleaseUdpSocket(SOCKET game)
{
	std::lock_guard<std::mutex> lock(handlesMutex);
	handles.erase(game);
	if (handles.empty())
		haveHandles.store(false, std::memory_order_release);
}

void ResumeUdpSocket(SOCKET game)
{
	auto socket = FindUdp(game);
	if (socket)
	{
		std::lock_guard<std::mutex> lock(ioMutex);
		socket->closing = false;
	}
}

void CancelAllUdpSockets()
{
	std::unique_lock<std::mutex> lock(ioMutex);
	{
		std::lock_guard<std::mutex> handlesLock(handlesMutex);
		for (const auto &item : handles)
		{
			item.second->closing = true;
			realCancelIoEx(reinterpret_cast<HANDLE>(item.second->backing), nullptr);
		}
	}
	finished.wait(lock, [] { return operations.empty(); });
}

void ReleaseAllUdpSockets()
{
	std::lock_guard<std::mutex> lock(handlesMutex);
	handles.clear();
	haveHandles.store(false, std::memory_order_release);
}

void ResumeAllUdpSockets()
{
	std::lock_guard<std::mutex> ioLock(ioMutex);
	std::lock_guard<std::mutex> handlesLock(handlesMutex);
	for (const auto &item : handles)
		item.second->closing = false;
}

void StopUdpHooks()
{
	haveHandles.store(false, std::memory_order_release);
	havePorts.store(false, std::memory_order_release);
}

int StartUdpIo(SOCKET game, LPWSAOVERLAPPED overlap, LPWSAOVERLAPPED_COMPLETION_ROUTINE callback, sockaddr *source,
               int *sourceLength, void *context, UdpSubmission submit, LPOVERLAPPED_COMPLETION_ROUTINE fileCallback,
               bool fileIo)
{
	const int invalidArgument = fileIo ? ERROR_INVALID_PARAMETER : WSAEINVAL;
	const int invalidHandle = fileIo ? ERROR_INVALID_HANDLE : WSAENOTSOCK;
	const int noMemory = fileIo ? ERROR_NOT_ENOUGH_MEMORY : WSAENOBUFS;
	auto socket = FindUdp(game);
	if (!socket || !overlap)
		return SocketError(socket ? invalidArgument : invalidHandle);
	try
	{
		auto owned = std::make_unique<Operation>();
		auto &op = *owned;
		op.socket = socket;
		op.user = overlap;
		op.source = source;
		op.sourceLength = sourceLength;
		op.fileIo = fileIo;
		ULONG_PTR bits = reinterpret_cast<ULONG_PTR>(overlap->hEvent);
		op.allowPort = (bits & 1) == 0;
		if (callback || fileCallback)
		{
			op.fileCallback = fileCallback != nullptr;
			op.callback = fileCallback ? CaptureUdpFileCallback(socket->identity, fileCallback)
			                           : CaptureUdpCallback(socket->identity, callback);
			if (!op.callback)
				return SocketError(noMemory);
		}
		else if (bits)
		{
			HANDLE original = reinterpret_cast<HANDLE>(bits & ~ULONG_PTR(1));
			if (!original || !DuplicateHandle(GetCurrentProcess(), original, GetCurrentProcess(), &op.userEvent, 0,
			                                  FALSE, DUPLICATE_SAME_ACCESS))
				return SocketError(original ? static_cast<int>(GetLastError()) : invalidHandle);
		}
		op.kernelEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
		op.wait = CreateThreadpoolWait(Completed, &op, nullptr);
		if (!op.kernelEvent || !op.wait)
			return SocketError(noMemory);
		op.kernel = *overlap;
		op.kernel.hEvent = reinterpret_cast<HANDLE>(reinterpret_cast<ULONG_PTR>(op.kernelEvent) | 1);
		int result, error;
		bool signaled, restoreEvent;
		LONG previousFileSignal = 0;
		{
			std::lock_guard<std::mutex> lock(ioMutex);
			if (socket->closing)
				return SocketError(invalidHandle);
			if (op.callback && socket->port)
				return SocketError(invalidArgument);
			if (operations.size() >= 4096)
				return SocketError(fileIo ? ERROR_NO_SYSTEM_RESOURCES : WSAENOBUFS);
			if (!operations.try_emplace(overlap, std::move(owned)).second)
				return SocketError(invalidArgument);
			LONG wasSignaled = 0;
			if (op.userEvent && resetEventState(op.userEvent, &wasSignaled) < 0)
			{
				operations.erase(overlap);
				return SocketError(WSA_INVALID_HANDLE);
			}
			++socket->outstanding;
			ULONG_PTR previous = op.kernel.Internal;
			overlap->Internal = Pending;
			op.signalEpoch = ++socket->signalEpoch;
			resetEventState(socket->visible, &previousFileSignal);
			result = submit(context, socket->backing, &op.kernel);
			error = WSAGetLastError();
			if (result == SOCKET_ERROR && error == WSA_IO_PENDING)
			{
				SetThreadpoolWait(op.wait, op.kernelEvent, nullptr);
				return SocketError(error);
			}
			signaled = WaitForSingleObject(op.kernelEvent, 0) == WAIT_OBJECT_0;
			restoreEvent = wasSignaled && result && !signaled && op.kernel.Internal == previous;
		}
		bool fileWarning = fileCallback && signaled && result == SOCKET_ERROR && error == ERROR_MORE_DATA &&
		                   static_cast<LONG>(op.kernel.Internal) == BufferOverflow;
		bool accepted = !result || fileWarning;
		Publish(op, accepted, accepted || signaled, accepted || error == WSAEMSGSIZE, restoreEvent,
		        previousFileSignal != 0);
		WSASetLastError(error);
		return fileWarning ? 0 : result;
	}
	catch (...)
	{
		return SocketError(noMemory);
	}
}

LONG AttachUdpSocketHooks()
{
	HMODULE nt = GetModuleHandleW(L"ntdll.dll");
#define LOAD(variable, name) variable = reinterpret_cast<decltype(variable)>(GetProcAddress(nt, name))
	LOAD(realSetFileInformation, "NtSetInformationFile");
	LOAD(setIoCompletion, "NtSetIoCompletion");
	LOAD(realCloseObject, "NtClose");
	LOAD(realDuplicateObject, "NtDuplicateObject");
	LOAD(queryObject, "NtQueryObject");
	LOAD(compareObjects, "NtCompareObjects");
	LOAD(resetEventState, "NtResetEvent");
	LOAD(statusToError, "RtlNtStatusToDosError");
#undef LOAD
	if (!realSetFileInformation || !setIoCompletion || !realCloseObject || !realDuplicateObject || !queryObject ||
	    !compareObjects || !resetEventState || !statusToError)
		return ERROR_PROC_NOT_FOUND;
	LONG error = NO_ERROR;
#define HOOK(name)                                                                                                     \
	if (!error)                                                                                                        \
	error = Attach(real##name, Hook##name)
	HOOK(SetFileInformation);
	HOOK(CloseObject);
	HOOK(DuplicateObject);
	HOOK(CancelIoEx);
	HOOK(CancelIo);
	HOOK(WsaResult);
	HOOK(Recv);
	HOOK(WSARecv);
	HOOK(ReadFile);
	HOOK(WriteFile);
	HOOK(ReadFileEx);
	HOOK(WriteFileEx);
	HOOK(GetFileType);
#undef HOOK
	return error;
}
