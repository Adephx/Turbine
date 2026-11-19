#pragma once
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <cstdint>
#include <cstring>
#include "detours.h"

extern uint32_t virtualAddress;
extern uint32_t virtualMask;
inline constexpr ULONG VirtualAdapterIndex = 0x5455;
bool GetVirtualMulticastInterface(SOCKET socket, uint32_t &value);
LONG AttachAdapterHooks();
LONG AttachLanHooks();
LONG AttachNameHooks();
LONG AttachConnectionHelpers();
bool ResolveLanName(const wchar_t *name, int family, wchar_t (&text)[64], bool allowMulticast);
LONG AttachChildHooks();
int ChildRequest(BYTE opcode, const BYTE *data, uint32_t size, BYTE *reply, uint32_t &replySize);
bool IsOverlayAddress(uint32_t address);
int SocketError(int error);

template <class T> LONG Attach(T &original, T replacement)
{
	return DetourAttach(reinterpret_cast<PVOID *>(&original), reinterpret_cast<PVOID>(replacement));
}
