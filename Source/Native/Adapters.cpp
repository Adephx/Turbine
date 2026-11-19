#include "Hook.h"
#include <array>

namespace
{
auto realGetAdaptersAddresses = ::GetAdaptersAddresses;
auto realGetAdaptersInfo = ::GetAdaptersInfo;
auto realGetIpAddrTable = ::GetIpAddrTable;
auto realGetIfTable = ::GetIfTable;
auto realGetIfEntry = ::GetIfEntry;
auto realGetBestInterface = ::GetBestInterface;
auto realGetBestInterfaceEx = ::GetBestInterfaceEx;
auto realGetHostByName = ::gethostbyname;
auto realGetAddrInfo = ::getaddrinfo;
auto realGetAddrInfoW = ::GetAddrInfoW;
auto realGetAddrInfoExW = ::GetAddrInfoExW;
auto realGetAddrInfoExA = ::GetAddrInfoExA;

struct AdapterBuffer
{
	IP_ADAPTER_ADDRESSES adapter;
	IP_ADAPTER_UNICAST_ADDRESS unicast;
	IP_ADAPTER_PREFIX prefix;
	sockaddr_in address;
	sockaddr_in subnet;
	char name[16];
	wchar_t friendlyName[16];
	wchar_t description[32];
	wchar_t suffix[1];
};

ULONG PrefixLength()
{
	ULONG prefix = 0;
	uint32_t mask = virtualMask;
	while (mask & 0x80000000u)
	{
		++prefix;
		mask <<= 1;
	}
	return prefix;
}

void FormatIpv4Address(uint32_t address, char *output, size_t capacity)
{
	sprintf_s(output, capacity, "%u.%u.%u.%u", (address >> 24) & 255, (address >> 16) & 255, (address >> 8) & 255,
	          address & 255);
}

void PhysicalAddress(BYTE *address)
{
	address[0] = 0x02;
	address[1] = 0x54;
	address[2] = static_cast<BYTE>(virtualAddress >> 24);
	address[3] = static_cast<BYTE>(virtualAddress >> 16);
	address[4] = static_cast<BYTE>(virtualAddress >> 8);
	address[5] = static_cast<BYTE>(virtualAddress);
}

ULONG WINAPI HookGetAdaptersAddresses(ULONG family, ULONG flags, PVOID reserved, PIP_ADAPTER_ADDRESSES output,
                                      PULONG size)
{
	if (reserved || !size)
		return ERROR_INVALID_PARAMETER;
	if (family == AF_INET6)
		return ERROR_NO_DATA;
	if (family != AF_UNSPEC && family != AF_INET)
		return ERROR_INVALID_PARAMETER;
	ULONG supplied = *size;
	*size = sizeof(AdapterBuffer);
	if (!output || supplied < sizeof(AdapterBuffer))
		return ERROR_BUFFER_OVERFLOW;
	auto *buffer = reinterpret_cast<AdapterBuffer *>(output);
	ZeroMemory(buffer, sizeof(*buffer));
	auto &adapter = buffer->adapter;
	adapter.Length = sizeof(adapter);
	adapter.IfIndex = VirtualAdapterIndex;
	strcpy_s(buffer->name, "Turbine");
	wcscpy_s(buffer->friendlyName, L"Turbine");
	wcscpy_s(buffer->description, L"Turbine virtual network");
	adapter.AdapterName = buffer->name;
	if (!(flags & GAA_FLAG_SKIP_FRIENDLY_NAME))
		adapter.FriendlyName = buffer->friendlyName;
	adapter.Description = buffer->description;
	adapter.DnsSuffix = buffer->suffix;
	adapter.Mtu = 65507;
	adapter.IfType = IF_TYPE_ETHERNET_CSMACD;
	adapter.OperStatus = IfOperStatusUp;
	adapter.PhysicalAddressLength = 6;
	PhysicalAddress(adapter.PhysicalAddress);
	adapter.Flags = IP_ADAPTER_IPV4_ENABLED;
	adapter.TransmitLinkSpeed = adapter.ReceiveLinkSpeed = 1000000000;
	adapter.Ipv4Metric = 1;
	adapter.Luid.Info.IfType = IF_TYPE_ETHERNET_CSMACD;
	adapter.Luid.Info.NetLuidIndex = VirtualAdapterIndex;
	adapter.ConnectionType = NET_IF_CONNECTION_DEDICATED;
	adapter.TunnelType = TUNNEL_TYPE_NONE;
	buffer->address.sin_family = AF_INET;
	buffer->address.sin_addr.s_addr = htonl(virtualAddress);
	buffer->subnet.sin_family = AF_INET;
	buffer->subnet.sin_addr.s_addr = htonl(virtualAddress & virtualMask);
	if (!(flags & GAA_FLAG_SKIP_UNICAST))
	{
		adapter.FirstUnicastAddress = &buffer->unicast;
		buffer->unicast.Length = sizeof(buffer->unicast);
		buffer->unicast.Address.lpSockaddr = reinterpret_cast<sockaddr *>(&buffer->address);
		buffer->unicast.Address.iSockaddrLength = sizeof(buffer->address);
		buffer->unicast.PrefixOrigin = IpPrefixOriginManual;
		buffer->unicast.SuffixOrigin = IpSuffixOriginManual;
		buffer->unicast.DadState = IpDadStatePreferred;
		buffer->unicast.ValidLifetime = buffer->unicast.PreferredLifetime = 0xffffffff;
		buffer->unicast.OnLinkPrefixLength = static_cast<UINT8>(PrefixLength());
	}
	if (flags & GAA_FLAG_INCLUDE_PREFIX)
	{
		adapter.FirstPrefix = &buffer->prefix;
		buffer->prefix.Length = sizeof(buffer->prefix);
		buffer->prefix.PrefixLength = PrefixLength();
		buffer->prefix.Address.lpSockaddr = reinterpret_cast<sockaddr *>(&buffer->subnet);
		buffer->prefix.Address.iSockaddrLength = sizeof(buffer->subnet);
	}
	return NO_ERROR;
}

ULONG WINAPI HookGetAdaptersInfo(PIP_ADAPTER_INFO output, PULONG size)
{
	if (!size)
		return ERROR_INVALID_PARAMETER;
	ULONG supplied = *size;
	*size = sizeof(IP_ADAPTER_INFO);
	if (!output || supplied < sizeof(IP_ADAPTER_INFO))
		return ERROR_BUFFER_OVERFLOW;
	ZeroMemory(output, sizeof(*output));
	strcpy_s(output->AdapterName, "Turbine");
	strcpy_s(output->Description, "Turbine virtual network");
	output->Index = VirtualAdapterIndex;
	output->Type = MIB_IF_TYPE_ETHERNET;
	output->AddressLength = 6;
	PhysicalAddress(output->Address);
	FormatIpv4Address(virtualAddress, output->IpAddressList.IpAddress.String,
	                  sizeof(output->IpAddressList.IpAddress.String));
	FormatIpv4Address(virtualMask, output->IpAddressList.IpMask.String, sizeof(output->IpAddressList.IpMask.String));
	output->CurrentIpAddress = &output->IpAddressList;
	return NO_ERROR;
}

DWORD WINAPI HookGetIpAddrTable(PMIB_IPADDRTABLE output, PULONG size, BOOL)
{
	if (!size)
		return ERROR_INVALID_PARAMETER;
	ULONG supplied = *size;
	*size = sizeof(MIB_IPADDRTABLE);
	if (!output || supplied < sizeof(MIB_IPADDRTABLE))
		return ERROR_INSUFFICIENT_BUFFER;
	ZeroMemory(output, sizeof(*output));
	output->dwNumEntries = 1;
	output->table[0].dwAddr = htonl(virtualAddress);
	output->table[0].dwMask = htonl(virtualMask);
	output->table[0].dwIndex = VirtualAdapterIndex;
	output->table[0].dwBCastAddr = 1;
	output->table[0].wType = MIB_IPADDR_PRIMARY;
	return NO_ERROR;
}

void FillInterfaceRow(MIB_IFROW &row)
{
	ZeroMemory(&row, sizeof(row));
	wcscpy_s(row.wszName, L"Turbine");
	row.dwIndex = VirtualAdapterIndex;
	row.dwType = MIB_IF_TYPE_ETHERNET;
	row.dwMtu = 65507;
	row.dwSpeed = 1000000000;
	row.dwPhysAddrLen = 6;
	PhysicalAddress(row.bPhysAddr);
	row.dwAdminStatus = MIB_IF_ADMIN_STATUS_UP;
	row.dwOperStatus = MIB_IF_OPER_STATUS_OPERATIONAL;
	const char description[] = "Turbine virtual network";
	memcpy(row.bDescr, description, sizeof(description));
	row.dwDescrLen = sizeof(description);
}
DWORD WINAPI HookGetIfTable(PMIB_IFTABLE output, PULONG size, BOOL)
{
	if (!size)
		return ERROR_INVALID_PARAMETER;
	ULONG supplied = *size;
	*size = sizeof(MIB_IFTABLE);
	if (!output || supplied < sizeof(MIB_IFTABLE))
		return ERROR_INSUFFICIENT_BUFFER;
	ZeroMemory(output, sizeof(*output));
	output->dwNumEntries = 1;
	FillInterfaceRow(output->table[0]);
	return NO_ERROR;
}
DWORD WINAPI HookGetIfEntry(PMIB_IFROW row)
{
	if (!row)
		return ERROR_INVALID_PARAMETER;
	if (row->dwIndex != VirtualAdapterIndex)
		return ERROR_NOT_FOUND;
	FillInterfaceRow(*row);
	return NO_ERROR;
}
DWORD WINAPI HookGetBestInterface(IPAddr destination, PDWORD index)
{
	if (!index)
		return ERROR_INVALID_PARAMETER;
	if (!IsOverlayAddress(ntohl(destination)))
		return ERROR_NETWORK_UNREACHABLE;
	*index = VirtualAdapterIndex;
	return NO_ERROR;
}
DWORD WINAPI HookGetBestInterfaceEx(sockaddr *destination, PDWORD index)
{
	if (!destination || destination->sa_family != AF_INET)
		return ERROR_INVALID_PARAMETER;
	return HookGetBestInterface(reinterpret_cast<sockaddr_in *>(destination)->sin_addr.s_addr, index);
}

bool IsLocalHostName(const wchar_t *name)
{
	if (!name)
		return false;
	wchar_t hostname[256]{};
	DWORD length = static_cast<DWORD>(std::size(hostname));
	return (virtualMask && !_wcsicmp(name, L"Turbine")) ||
	       (GetComputerNameW(hostname, &length) && !_wcsicmp(name, hostname));
}

bool ResolveName(const wchar_t *name, int family, bool numericOnly, wchar_t (&out)[64], const wchar_t *&resolved)
{
	resolved = name;
	if (!name || !*name || !_wcsicmp(name, L"localhost"))
		return true;
	if (IsLocalHostName(name))
	{
		if (!virtualMask)
			return true;
		if (family != AF_INET && family != AF_UNSPEC)
			return false;
		swprintf_s(out, L"%u.%u.%u.%u", virtualAddress >> 24, (virtualAddress >> 16) & 255, (virtualAddress >> 8) & 255,
		           virtualAddress & 255);
		resolved = out;
		return true;
	}
	in_addr v4{};
	in6_addr v6{};
	wchar_t literal[64]{};
	size_t count = wcscspn(name, L"%");
	if (count < std::size(literal))
	{
		wmemcpy(literal, name, count);
		if ((!name[count] && InetPtonW(AF_INET, literal, &v4) == 1) || InetPtonW(AF_INET6, literal, &v6) == 1)
			return true;
	}
	if (!virtualMask && !numericOnly)
	{
		auto dot = wcschr(name, L'.');
		auto length = wcslen(name);
		bool local = !dot || (length > 6 && !_wcsicmp(name + length - 6, L".local"));
		if (ResolveLanName(name, family, out, false) || (local && ResolveLanName(name, family, out, true)))
		{
			resolved = out;
			return true;
		}
	}
	return false;
}

bool ResolveName(const char *name, int family, bool numericOnly, char (&out)[64], const char *&resolved)
{
	resolved = name;
	if (!name)
		return true;
	wchar_t wide[256]{}, result[64]{};
	const wchar_t *value;
	if (!MultiByteToWideChar(CP_ACP, 0, name, -1, wide, static_cast<int>(std::size(wide))) ||
	    !ResolveName(wide, family, numericOnly, result, value))
		return false;
	if (value == result)
	{
		if (!WideCharToMultiByte(CP_ACP, 0, result, -1, out, sizeof(out), nullptr, nullptr))
			return false;
		resolved = out;
	}
	return true;
}

hostent *WSAAPI HookGetHostByName(const char *name)
{
	char out[64]{};
	if (!ResolveName(name, AF_INET, false, out, name))
	{
		WSASetLastError(WSAHOST_NOT_FOUND);
		return nullptr;
	}
	return realGetHostByName(name);
}

template <class C, class H, class F> int Lookup(F fn, const C *name, const C *service, const H *hints, H **result)
{
	C out[64]{};
	if (!ResolveName(name, hints ? hints->ai_family : AF_UNSPEC, hints && (hints->ai_flags & AI_NUMERICHOST), out,
	                 name))
	{
		if (result)
			*result = nullptr;
		WSASetLastError(WSAHOST_NOT_FOUND);
		return WSAHOST_NOT_FOUND;
	}
	return fn(name, service, hints, result);
}

INT WSAAPI HookGetAddrInfo(PCSTR name, PCSTR service, const ADDRINFOA *hints, PADDRINFOA *result)
{
	return Lookup(realGetAddrInfo, name, service, hints, result);
}

INT WSAAPI HookGetAddrInfoW(PCWSTR name, PCWSTR service, const ADDRINFOW *hints, PADDRINFOW *result)
{
	return Lookup(realGetAddrInfoW, name, service, hints, result);
}

template <class C, class H, class F>
INT LookupEx(F fn, const C *name, const C *service, DWORD ns, LPGUID id, const H *hints, H **result, timeval *timeout,
             LPOVERLAPPED ov, LPLOOKUPSERVICE_COMPLETION_ROUTINE cb, LPHANDLE handle)
{
	C out[64]{};
	if (!ResolveName(name, hints ? hints->ai_family : AF_UNSPEC, hints && (hints->ai_flags & AI_NUMERICHOST), out,
	                 name))
	{
		if (result)
			*result = nullptr;
		WSASetLastError(WSAHOST_NOT_FOUND);
		return WSAHOST_NOT_FOUND;
	}
	return fn(name, service, ns, id, hints, result, timeout, ov, cb, handle);
}
INT WSAAPI HookGetAddrInfoExA(PCSTR name, PCSTR service, DWORD ns, LPGUID id, const ADDRINFOEXA *hints,
                              PADDRINFOEXA *result, timeval *timeout, LPOVERLAPPED ov,
                              LPLOOKUPSERVICE_COMPLETION_ROUTINE cb, LPHANDLE handle)
{
	return LookupEx(realGetAddrInfoExA, name, service, ns, id, hints, result, timeout, ov, cb, handle);
}
INT WSAAPI HookGetAddrInfoExW(PCWSTR name, PCWSTR service, DWORD ns, LPGUID id, const ADDRINFOEXW *hints,
                              PADDRINFOEXW *result, timeval *timeout, LPOVERLAPPED ov,
                              LPLOOKUPSERVICE_COMPLETION_ROUTINE cb, LPHANDLE handle)
{
	return LookupEx(realGetAddrInfoExW, name, service, ns, id, hints, result, timeout, ov, cb, handle);
}
}

LONG AttachNameHooks()
{
	LONG error = NO_ERROR;
#define HOOK(name)                                                                                                     \
	if (!error)                                                                                                        \
	error = Attach(real##name, Hook##name)
	HOOK(GetHostByName);
	HOOK(GetAddrInfo);
	HOOK(GetAddrInfoW);
	HOOK(GetAddrInfoExA);
	HOOK(GetAddrInfoExW);
	return error;
}

LONG AttachAdapterHooks()
{
	LONG error = AttachNameHooks();
	HOOK(GetAdaptersAddresses);
	HOOK(GetAdaptersInfo);
	HOOK(GetIpAddrTable);
	HOOK(GetIfTable);
	HOOK(GetIfEntry);
	HOOK(GetBestInterface);
	HOOK(GetBestInterfaceEx);
#undef HOOK
	return error;
}
