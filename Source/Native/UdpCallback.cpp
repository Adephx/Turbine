#include "UdpCallback.h"
#include <limits>
#include <mutex>
#include <unordered_map>
#include <utility>

namespace
{
std::mutex callbackMutex;
std::unordered_map<ULONG_PTR, std::weak_ptr<UdpCallback>> callbacks;
ULONG_PTR nextCallback = 1;
thread_local UdpCallback *activeCallback = nullptr;
thread_local std::shared_ptr<UdpCallback> deferredFirst;
thread_local UdpCallback *deferredLast = nullptr;

enum class CallbackState
{
	Captured,
	Queued,
	Deferred,
	Delivering,
	Finished,
	Discarded
};
}

struct UdpCallback
{
	uint64_t socketIdentity = 0;
	HANDLE thread = nullptr;
	PTP_WAIT exitWait = nullptr;
	ULONG_PTR id = 0;
	LPWSAOVERLAPPED_COMPLETION_ROUTINE completion = nullptr;
	LPOVERLAPPED_COMPLETION_ROUTINE fileCompletion = nullptr;
	LPWSAOVERLAPPED overlap = nullptr;
	DWORD error = 0;
	DWORD bytes = 0;
	DWORD flags = 0;
	CallbackState state = CallbackState::Captured;
	std::shared_ptr<UdpCallback> queued;
	std::shared_ptr<UdpCallback> next;
	UdpCallback *previousActive = nullptr;

	~UdpCallback()
	{
		DWORD savedError = GetLastError();
		{
			std::lock_guard<std::mutex> lock(callbackMutex);
			auto found = callbacks.find(id);
			if (found != callbacks.end() && found->second.expired())
				callbacks.erase(found);
		}
		if (exitWait)
		{
			SetThreadpoolWait(exitWait, nullptr, nullptr);
			CloseThreadpoolWait(exitWait);
		}
		if (thread)
			CloseHandle(thread);
		SetLastError(savedError);
	}
};

namespace
{
bool IsActive(uint64_t socketIdentity)
{
	for (auto *current = activeCallback; current; current = current->previousActive)
		if (!current->fileCompletion && current->socketIdentity == socketIdentity)
			return true;
	return false;
}

void CALLBACK ThreadExited(PTP_CALLBACK_INSTANCE, void *context, PTP_WAIT, TP_WAIT_RESULT)
{
	std::shared_ptr<UdpCallback> target;
	{
		std::lock_guard<std::mutex> lock(callbackMutex);
		auto found = callbacks.find(reinterpret_cast<ULONG_PTR>(context));
		if (found == callbacks.end())
			return;
		target = found->second.lock();
		if (target)
		{
			target->state = CallbackState::Discarded;
			target->queued.reset();
		}
		callbacks.erase(found);
	}
}

void Invoke(const std::shared_ptr<UdpCallback> &target)
{
	{
		std::lock_guard<std::mutex> lock(callbackMutex);
		if (target->state != CallbackState::Queued && target->state != CallbackState::Deferred)
			return;
		if (!target->fileCompletion && IsActive(target->socketIdentity))
		{
			target->state = CallbackState::Deferred;
			if (deferredLast)
				deferredLast->next = target;
			else
				deferredFirst = target;
			deferredLast = target.get();
			target->queued.reset();
			return;
		}
		target->state = CallbackState::Delivering;
		target->queued.reset();
	}
	struct ActiveScope
	{
		UdpCallback &target;
		explicit ActiveScope(UdpCallback &value) : target(value)
		{
			target.previousActive = activeCallback;
			activeCallback = &target;
		}
		~ActiveScope()
		{
			activeCallback = target.previousActive;
			target.previousActive = nullptr;
		}
	};
	{
		ActiveScope active(*target);
		if (target->fileCompletion)
			target->fileCompletion(target->error, target->bytes, target->overlap);
		else
			target->completion(target->error, target->bytes, target->overlap, target->flags);
	}
	{
		std::lock_guard<std::mutex> lock(callbackMutex);
		target->state = CallbackState::Finished;
		callbacks.erase(target->id);
	}
}

void DrainDeferred()
{
	for (;;)
	{
		std::shared_ptr<UdpCallback> *link = &deferredFirst;
		UdpCallback *previous = nullptr;
		while (*link && IsActive((*link)->socketIdentity))
		{
			previous = link->get();
			link = &(*link)->next;
		}
		if (!*link)
			return;
		auto target = std::move(*link);
		*link = std::move(target->next);
		if (deferredLast == target.get())
			deferredLast = previous;
		Invoke(target);
	}
}

void CALLBACK Deliver(ULONG_PTR id)
{
	std::shared_ptr<UdpCallback> target;
	{
		std::lock_guard<std::mutex> lock(callbackMutex);
		auto found = callbacks.find(id);
		if (found != callbacks.end())
			target = found->second.lock();
	}
	if (target)
		Invoke(target);
	DrainDeferred();
}
}

static std::shared_ptr<UdpCallback> CaptureCallback(uint64_t socketIdentity,
                                                    LPWSAOVERLAPPED_COMPLETION_ROUTINE completion,
                                                    LPOVERLAPPED_COMPLETION_ROUTINE fileCompletion)
{
	if (!completion && !fileCompletion)
	{
		WSASetLastError(WSAEINVAL);
		return {};
	}
	try
	{
		auto target = std::make_shared<UdpCallback>();
		target->socketIdentity = socketIdentity;
		target->completion = completion;
		target->fileCompletion = fileCompletion;
		if (!DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &target->thread, 0, FALSE,
		                     DUPLICATE_SAME_ACCESS))
		{
			WSASetLastError(WSAENOBUFS);
			return {};
		}
		{
			std::lock_guard<std::mutex> lock(callbackMutex);
			// Never reuse an APC identifier: a discarded APC can remain queued on a live thread.
			if (nextCallback == std::numeric_limits<ULONG_PTR>::max())
				throw std::bad_alloc();
			target->id = nextCallback++;
			callbacks.emplace(target->id, target);
		}
		// Wait callbacks carry identifiers rather than pointers so queued callbacks survive cancellation safely.
		target->exitWait = CreateThreadpoolWait(ThreadExited, reinterpret_cast<void *>(target->id), nullptr);
		if (!target->exitWait)
		{
			WSASetLastError(WSAENOBUFS);
			return {};
		}
		SetThreadpoolWait(target->exitWait, target->thread, nullptr);
		return target;
	}
	catch (...)
	{
		WSASetLastError(WSAENOBUFS);
		return {};
	}
}

std::shared_ptr<UdpCallback> CaptureUdpCallback(uint64_t socketIdentity, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
	return CaptureCallback(socketIdentity, completion, nullptr);
}

std::shared_ptr<UdpCallback> CaptureUdpFileCallback(uint64_t socketIdentity, LPOVERLAPPED_COMPLETION_ROUTINE completion)
{
	return CaptureCallback(socketIdentity, nullptr, completion);
}

void QueueUdpCallback(const std::shared_ptr<UdpCallback> &target, DWORD error, DWORD bytes, LPWSAOVERLAPPED overlap,
                      DWORD flags)
{
	if (!target)
		return;
	std::lock_guard<std::mutex> lock(callbackMutex);
	if (target->state != CallbackState::Captured || !callbacks.count(target->id))
		return;
	target->error = error;
	target->bytes = bytes;
	target->overlap = overlap;
	target->flags = flags;
	target->state = CallbackState::Queued;
	target->queued = target;
	if (!QueueUserAPC(Deliver, target->thread, target->id))
	{
		target->state = CallbackState::Discarded;
		target->queued.reset();
		callbacks.erase(target->id);
	}
}

void DiscardUdpCallbacks()
{
	for (;;)
	{
		std::shared_ptr<UdpCallback> target;
		{
			std::lock_guard<std::mutex> lock(callbackMutex);
			if (callbacks.empty())
				break;
			auto found = callbacks.begin();
			target = found->second.lock();
			if (target)
			{
				target->state = CallbackState::Discarded;
				target->queued.reset();
			}
			callbacks.erase(found);
		}
	}
}
