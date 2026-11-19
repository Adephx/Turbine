#include "Hook.h"
#include <bcrypt.h>
#include <sddl.h>
#include <algorithm>
#include <array>
#include <string>
#include <vector>

namespace
{
auto realCreateProcessA = ::CreateProcessA;
auto realCreateProcessW = ::CreateProcessW;

std::wstring WideText(const char *value, UINT codePage = CP_ACP, int length = -1)
{
	if (!value)
		return {};
	int size = MultiByteToWideChar(codePage, 0, value, length, nullptr, 0);
	if (!size)
		return {};
	std::wstring result(static_cast<size_t>(size), L'\0');
	if (!MultiByteToWideChar(codePage, 0, value, length, result.data(), size))
		return {};
	if (length < 0)
		result.resize(result.size() - 1);
	return result;
}

std::wstring ReadEnvironment(const wchar_t *name)
{
	DWORD length = GetEnvironmentVariableW(name, nullptr, 0);
	if (!length || length > 32767)
		return {};
	std::wstring result(length, L'\0');
	DWORD written = GetEnvironmentVariableW(name, result.data(), length);
	if (!written || written >= length)
		return {};
	result.resize(written);
	return result;
}

std::string DllInjectionPath(const std::wstring &path)
{
	if (path.empty())
		return {};
	auto convert = [](const std::wstring &text) {
		const UINT codePage = GetACP();
		const bool utf8 = codePage == CP_UTF8;
		const DWORD flags = utf8 ? WC_ERR_INVALID_CHARS : WC_NO_BEST_FIT_CHARS;
		BOOL substituted = FALSE;
		BOOL *usedDefault = utf8 ? nullptr : &substituted;
		int length = WideCharToMultiByte(codePage, flags, text.c_str(), -1, nullptr, 0, nullptr, usedDefault);
		if (!length || substituted)
			return std::string{};
		std::string result(length, '\0');
		if (!WideCharToMultiByte(codePage, flags, text.c_str(), -1, result.data(), length, nullptr, usedDefault) ||
		    substituted)
			return std::string{};
		result.resize(result.size() - 1);
		return result;
	};
	auto result = convert(path);
	if (!result.empty())
		return result;
	DWORD length = GetShortPathNameW(path.c_str(), nullptr, 0);
	if (!length)
		return {};
	std::wstring shortPath(length, L'\0');
	DWORD written = GetShortPathNameW(path.c_str(), shortPath.data(), length);
	if (!written || written >= length)
		return {};
	shortPath.resize(written);
	return convert(shortPath);
}

HANDLE CreateReadyEvent(std::wstring &name)
{
	BYTE random[16]{};
	if (BCryptGenRandom(nullptr, random, sizeof(random), BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0)
	{
		SetLastError(ERROR_GEN_FAILURE);
		return nullptr;
	}
	const wchar_t *hex = L"0123456789abcdef";
	name = L"Local\\Turbine-";
	for (BYTE value : random)
	{
		name += hex[value >> 4];
		name += hex[value & 15];
	}
	PSECURITY_DESCRIPTOR descriptor = nullptr;
	if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;OW)", SDDL_REVISION_1, &descriptor,
	                                                          nullptr))
		return nullptr;
	SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
	HANDLE result = CreateEventW(&attributes, TRUE, FALSE, name.c_str());
	DWORD error = GetLastError();
	LocalFree(descriptor);
	if (result && error == ERROR_ALREADY_EXISTS)
	{
		CloseHandle(result);
		result = nullptr;
	}
	SetLastError(error);
	return result;
}

std::vector<wchar_t> BuildChildEnvironment(LPVOID supplied, bool unicode, const std::wstring &pipe,
                                           const std::wstring &token, const std::wstring &ready)
{
	std::vector<std::wstring> entries;
	if (!supplied)
	{
		LPWCH inherited = GetEnvironmentStringsW();
		if (!inherited)
			return {};
		try
		{
			for (const wchar_t *item = inherited; *item; item += wcslen(item) + 1)
				entries.emplace_back(item);
		}
		catch (...)
		{
			FreeEnvironmentStringsW(inherited);
			throw;
		}
		FreeEnvironmentStringsW(inherited);
	}
	else if (unicode)
	{
		for (const wchar_t *item = static_cast<const wchar_t *>(supplied); *item; item += wcslen(item) + 1)
			entries.emplace_back(item);
	}
	else
	{
		for (const char *item = static_cast<const char *>(supplied); *item; item += strlen(item) + 1)
			entries.push_back(WideText(item));
	}
	auto set = [&](const wchar_t *key, const std::wstring &value) {
		std::wstring prefix = std::wstring(key) + L"=";
		entries.erase(std::remove_if(entries.begin(), entries.end(),
		                             [&](const std::wstring &entry) {
			                             return !_wcsnicmp(entry.c_str(), prefix.c_str(), prefix.size());
		                             }),
		              entries.end());
		entries.push_back(prefix + value);
	};
	set(L"TURBINE_PIPE", pipe);
	set(L"TURBINE_TOKEN", token);
	set(L"TURBINE_READY", ready);
	set(L"TURBINE_NATIVE_X86", ReadEnvironment(L"TURBINE_NATIVE_X86"));
	set(L"TURBINE_NATIVE_X64", ReadEnvironment(L"TURBINE_NATIVE_X64"));
	std::sort(entries.begin(), entries.end(),
	          [](const std::wstring &a, const std::wstring &b) { return _wcsicmp(a.c_str(), b.c_str()) < 0; });
	std::vector<wchar_t> result;
	for (const auto &entry : entries)
	{
		result.insert(result.end(), entry.begin(), entry.end());
		result.push_back(L'\0');
	}
	result.push_back(L'\0');
	return result;
}

int AuthorizeChild(const std::array<BYTE, 16> &token, DWORD processId)
{
	std::array<BYTE, 20> request{};
	memcpy(request.data(), token.data(), token.size());
	memcpy(request.data() + token.size(), &processId, sizeof(processId));
	uint32_t size = 0;
	return ChildRequest(10, request.data(), static_cast<uint32_t>(request.size()), nullptr, size);
}

BOOL WINAPI HookCreateProcessW(LPCWSTR application, LPWSTR command, LPSECURITY_ATTRIBUTES processAttributes,
                               LPSECURITY_ATTRIBUTES threadAttributes, BOOL inherit, DWORD flags, LPVOID environment,
                               LPCWSTR directory, LPSTARTUPINFOW startup, LPPROCESS_INFORMATION output)
{
	if (!startup || !output || startup->cb < sizeof(STARTUPINFOW) ||
	    ((flags & EXTENDED_STARTUPINFO_PRESENT) && startup->cb < sizeof(STARTUPINFOEXW)))
	{
		SetLastError(ERROR_INVALID_PARAMETER);
		return FALSE;
	}
	if (flags & (DEBUG_PROCESS | DEBUG_ONLY_THIS_PROCESS | CREATE_PROTECTED_PROCESS))
	{
		SetLastError(ERROR_NOT_SUPPORTED);
		return FALSE;
	}
	HANDLE ready = nullptr;
	PROCESS_INFORMATION process{};
	std::array<BYTE, 16> token{};
	bool prepared = false, authorized = false;
	DWORD failure = ERROR_GEN_FAILURE;
	try
	{
		std::array<BYTE, 258> reply{};
		uint32_t size = static_cast<uint32_t>(reply.size());
		int error = ChildRequest(9, nullptr, 0, reply.data(), size);
		uint16_t nameLength = 0;
		if (size >= sizeof(nameLength))
			memcpy(&nameLength, reply.data(), sizeof(nameLength));
		if (error || !nameLength || nameLength > 240 || size != sizeof(nameLength) + nameLength + token.size())
		{
			SetLastError(error ? static_cast<DWORD>(error) : ERROR_INVALID_DATA);
			return FALSE;
		}
		std::wstring childPipe =
		    WideText(reinterpret_cast<const char *>(reply.data() + sizeof(nameLength)), CP_UTF8, nameLength);
		memcpy(token.data(), reply.data() + sizeof(nameLength) + nameLength, token.size());
		prepared = true;
		if (childPipe.empty())
		{
			failure = ERROR_INVALID_DATA;
			throw failure;
		}
		std::wstring childToken;
		const wchar_t *hex = L"0123456789abcdef";
		for (BYTE value : token)
		{
			childToken += hex[value >> 4];
			childToken += hex[value & 15];
		}
		std::wstring readyName;
		ready = CreateReadyEvent(readyName);
		if (!ready)
		{
			failure = GetLastError();
			throw failure;
		}
		auto childEnvironment = BuildChildEnvironment(environment, (flags & CREATE_UNICODE_ENVIRONMENT) != 0, childPipe,
		                                              childToken, readyName);
		if (childEnvironment.empty())
		{
			failure = ERROR_ENVVAR_NOT_FOUND;
			throw failure;
		}
		if (!realCreateProcessW(application, command, processAttributes, threadAttributes, inherit,
		                        flags | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, childEnvironment.data(),
		                        directory, startup, &process))
		{
			failure = GetLastError();
			throw failure;
		}
		USHORT processMachine = 0, nativeMachine = 0;
		if (!IsWow64Process2(process.hProcess, &processMachine, &nativeMachine))
		{
			failure = GetLastError();
			throw failure;
		}
		USHORT architecture = processMachine == IMAGE_FILE_MACHINE_UNKNOWN ? nativeMachine : processMachine;
		if (architecture != IMAGE_FILE_MACHINE_I386 && architecture != IMAGE_FILE_MACHINE_AMD64)
		{
			failure = ERROR_NOT_SUPPORTED;
			throw failure;
		}
		auto path = DllInjectionPath(
		    ReadEnvironment(architecture == IMAGE_FILE_MACHINE_I386 ? L"TURBINE_NATIVE_X86" : L"TURBINE_NATIVE_X64"));
		if (path.empty())
		{
			failure = ERROR_NO_UNICODE_TRANSLATION;
			throw failure;
		}
		const char *dll = path.c_str();
		if (!DetourUpdateProcessWithDll(process.hProcess, &dll, 1) &&
		    !DetourProcessViaHelperW(process.dwProcessId, dll, realCreateProcessW))
		{
			failure = GetLastError();
			throw failure;
		}
		error = AuthorizeChild(token, process.dwProcessId);
		if (error)
		{
			failure = static_cast<DWORD>(error);
			throw failure;
		}
		authorized = true;
		if (!(flags & CREATE_SUSPENDED))
		{
			if (ResumeThread(process.hThread) == DWORD(-1))
			{
				failure = GetLastError();
				throw failure;
			}
			HANDLE waits[] = {ready, process.hProcess};
			DWORD wait = WaitForMultipleObjects(2, waits, FALSE, 30000);
			if (wait != WAIT_OBJECT_0)
			{
				failure = wait == WAIT_TIMEOUT ? WAIT_TIMEOUT : ERROR_DLL_INIT_FAILED;
				throw failure;
			}
		}
		else
		{
			HANDLE childEvent = nullptr;
			if (!DuplicateHandle(GetCurrentProcess(), ready, process.hProcess, &childEvent, EVENT_MODIFY_STATE, FALSE,
			                     0))
			{
				failure = GetLastError();
				throw failure;
			}
		}
		*output = process;
		CloseHandle(ready);
		return TRUE;
	}
	catch (DWORD)
	{
	}
	catch (...)
	{
		failure = ERROR_NOT_ENOUGH_MEMORY;
	}
	if (process.hProcess)
	{
		TerminateProcess(process.hProcess, failure);
		CloseHandle(process.hThread);
		CloseHandle(process.hProcess);
	}
	if (ready)
		CloseHandle(ready);
	if (prepared && !authorized)
		AuthorizeChild(token, 0);
	ZeroMemory(output, sizeof(*output));
	SetLastError(failure);
	return FALSE;
}

BOOL WINAPI HookCreateProcessA(LPCSTR application, LPSTR command, LPSECURITY_ATTRIBUTES processAttributes,
                               LPSECURITY_ATTRIBUTES threadAttributes, BOOL inherit, DWORD flags, LPVOID environment,
                               LPCSTR directory, LPSTARTUPINFOA startup, LPPROCESS_INFORMATION output)
{
	if (!startup || !output || startup->cb < sizeof(STARTUPINFOA) ||
	    ((flags & EXTENDED_STARTUPINFO_PRESENT) && startup->cb < sizeof(STARTUPINFOEXA)))
	{
		SetLastError(ERROR_INVALID_PARAMETER);
		return FALSE;
	}
	try
	{
		std::wstring app = WideText(application), cmd = WideText(command), cwd = WideText(directory);
		std::wstring desktop = WideText(startup->lpDesktop), title = WideText(startup->lpTitle),
		             reserved = WideText(startup->lpReserved);
		STARTUPINFOEXW converted{};
		static_assert(sizeof(STARTUPINFOA) == sizeof(STARTUPINFOW));
		memcpy(&converted.StartupInfo, startup, sizeof(STARTUPINFOA));
		converted.StartupInfo.lpDesktop = startup->lpDesktop ? desktop.data() : nullptr;
		converted.StartupInfo.lpTitle = startup->lpTitle ? title.data() : nullptr;
		converted.StartupInfo.lpReserved = startup->lpReserved ? reserved.data() : nullptr;
		if (flags & EXTENDED_STARTUPINFO_PRESENT)
			converted.lpAttributeList = reinterpret_cast<STARTUPINFOEXA *>(startup)->lpAttributeList;
		return HookCreateProcessW(application ? app.c_str() : nullptr, command ? cmd.data() : nullptr,
		                          processAttributes, threadAttributes, inherit, flags, environment,
		                          directory ? cwd.c_str() : nullptr, &converted.StartupInfo, output);
	}
	catch (...)
	{
		SetLastError(ERROR_NOT_ENOUGH_MEMORY);
		return FALSE;
	}
}
}

LONG AttachChildHooks()
{
	LONG error = Attach(realCreateProcessA, HookCreateProcessA);
	if (!error)
		error = Attach(realCreateProcessW, HookCreateProcessW);
	return error;
}
