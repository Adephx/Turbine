#pragma once
#include "Hook.h"
#include <type_traits>

SOCKET MakeUdpSocket(SOCKET backing);
SOCKET UdpBackingSocket(SOCKET socket);
void ResetUdpSocketSignal(SOCKET socket);
void CancelUdpSocket(SOCKET socket);
void ResumeUdpSocket(SOCKET socket);
void ReleaseUdpSocket(SOCKET socket);
void CancelAllUdpSockets();
void ResumeAllUdpSockets();
void ReleaseAllUdpSockets();
void TranslateUdpSource(sockaddr *address, int *length);
int CloseGameUdp(SOCKET socket);
void StopUdpHooks();
using UdpSubmission = int (*)(void *, SOCKET, LPWSAOVERLAPPED);
int StartUdpIo(SOCKET socket, LPWSAOVERLAPPED overlap, LPWSAOVERLAPPED_COMPLETION_ROUTINE callback, sockaddr *source,
               int *sourceLength, void *context, UdpSubmission submit, LPOVERLAPPED_COMPLETION_ROUTINE fileCallback,
               bool fileIo);
template <class Submit>
int SubmitUdpIo(SOCKET socket, LPWSAOVERLAPPED overlap, LPWSAOVERLAPPED_COMPLETION_ROUTINE callback, sockaddr *source,
                int *sourceLength, Submit &&submit, LPOVERLAPPED_COMPLETION_ROUTINE fileCallback = nullptr,
                bool fileIo = false)
{
	return StartUdpIo(
	    socket, overlap, callback, source, sourceLength, &submit,
	    [](void *context, SOCKET backing, LPWSAOVERLAPPED operation) {
		    return (*static_cast<std::remove_reference_t<Submit> *>(context))(backing, operation);
	    },
	    fileCallback, fileIo);
}
LONG AttachUdpSocketHooks();
LONG AttachUdpForwardHooks();
int UdpAsyncSelect(SOCKET socket, HWND window, unsigned int message, long events, decltype(&::WSAAsyncSelect) native);
void ForgetUdpAsyncSelect(SOCKET socket, SOCKET backing);
void ClearUdpAsyncSelect();
