#pragma once
#include "Hook.h"
#include <memory>

struct UdpCallback;
std::shared_ptr<UdpCallback> CaptureUdpCallback(uint64_t socketIdentity, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion);
std::shared_ptr<UdpCallback> CaptureUdpFileCallback(uint64_t socketIdentity,
                                                    LPOVERLAPPED_COMPLETION_ROUTINE completion);
void QueueUdpCallback(const std::shared_ptr<UdpCallback> &target, DWORD error, DWORD bytes, LPWSAOVERLAPPED overlap,
                      DWORD flags);
void DiscardUdpCallbacks();
