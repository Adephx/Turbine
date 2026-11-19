#include <winsock2.h>
#include <windows.h>
#include <bcrypt.h>
#include <sddl.h>
#include <cstdio>
#include <cstring>
#include <string>
#include "detours.h"

namespace
{
int Fail(DWORD error)
{
	std::printf("ERROR %lu\n", error);
	std::fflush(stdout);
	return 1;
}

std::string AnsiPath(const std::wstring &path)
{
	const UINT codePage = GetACP();
	const bool utf8 = codePage == CP_UTF8;
	const DWORD flags = utf8 ? WC_ERR_INVALID_CHARS : WC_NO_BEST_FIT_CHARS;
	BOOL substituted = FALSE;
	BOOL *usedDefault = utf8 ? nullptr : &substituted;
	int count = WideCharToMultiByte(codePage, flags, path.c_str(), -1, nullptr, 0, nullptr, usedDefault);
	if (!count || substituted)
		return {};
	std::string result(static_cast<size_t>(count), '\0');
	if (!WideCharToMultiByte(codePage, flags, path.c_str(), -1, result.data(), count, nullptr, usedDefault) ||
	    substituted)
		return {};
	result.resize(result.size() - 1);
	return result;
}
}

int wmain(int argc, wchar_t **argv)
{
	std::wstring application, directory, commandLine, dll;
	if (argc != 9)
		return Fail(ERROR_INVALID_PARAMETER);
	for (int i = 1; i + 1 < argc; i += 2)
	{
		if (!wcscmp(argv[i], L"--app"))
			application = argv[i + 1];
		else if (!wcscmp(argv[i], L"--cwd"))
			directory = argv[i + 1];
		else if (!wcscmp(argv[i], L"--command-line"))
			commandLine = argv[i + 1];
		else if (!wcscmp(argv[i], L"--dll"))
			dll = argv[i + 1];
		else
			return Fail(ERROR_INVALID_PARAMETER);
	}
	if (application.empty() || dll.empty() || commandLine.empty() || commandLine.size() > 32766)
		return Fail(ERROR_INVALID_PARAMETER);
	auto dllAnsi = AnsiPath(dll);
	if (dllAnsi.empty())
	{
		DWORD required = GetShortPathNameW(dll.c_str(), nullptr, 0);
		if (required)
		{
			std::wstring shortName(required, L'\0');
			DWORD written = GetShortPathNameW(dll.c_str(), shortName.data(), required);
			if (written && written < required)
			{
				shortName.resize(written);
				dllAnsi = AnsiPath(shortName);
			}
		}
	}
	if (dllAnsi.empty())
		return Fail(ERROR_NO_UNICODE_TRANSLATION);

	BYTE random[16];
	if (BCryptGenRandom(nullptr, random, sizeof(random), BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0)
		return Fail(ERROR_GEN_FAILURE);
	std::wstring eventName = L"Local\\Turbine-";
	const wchar_t *hex = L"0123456789abcdef";
	for (BYTE value : random)
	{
		eventName += hex[value >> 4];
		eventName += hex[value & 15];
	}
	PSECURITY_DESCRIPTOR descriptor = nullptr;
	if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;OW)", SDDL_REVISION_1, &descriptor,
	                                                          nullptr))
		return Fail(GetLastError());
	SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
	HANDLE ready = CreateEventW(&attributes, TRUE, FALSE, eventName.c_str());
	DWORD eventError = GetLastError();
	LocalFree(descriptor);
	if (!ready)
		return Fail(eventError);
	if (eventError == ERROR_ALREADY_EXISTS)
	{
		CloseHandle(ready);
		return Fail(ERROR_ALREADY_EXISTS);
	}
	if (!SetEnvironmentVariableW(L"TURBINE_READY", eventName.c_str()))
	{
		DWORD error = GetLastError();
		CloseHandle(ready);
		return Fail(error);
	}

	STARTUPINFOW startup{};
	startup.cb = sizeof(startup);
	PROCESS_INFORMATION process{};
	if (!DetourCreateProcessWithDllExW(application.c_str(), commandLine.data(), nullptr, nullptr, FALSE,
	                                   CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, nullptr,
	                                   directory.empty() ? nullptr : directory.c_str(), &startup, &process,
	                                   dllAnsi.c_str(), nullptr))
	{
		DWORD error = GetLastError();
		CloseHandle(ready);
		return Fail(error);
	}
	std::printf("PID %lu\n", process.dwProcessId);
	std::fflush(stdout);
	char approval[8]{};
	bool approved =
	    std::fgets(approval, sizeof(approval), stdin) && (!strcmp(approval, "GO\n") || !strcmp(approval, "GO\r\n"));
	DWORD result = ERROR_ACCESS_DENIED;
	if (approved && ResumeThread(process.hThread) == DWORD(-1))
		result = GetLastError();
	else if (approved)
	{
		HANDLE waits[] = {ready, process.hProcess};
		DWORD wait = WaitForMultipleObjects(2, waits, FALSE, 30000);
		if (wait == WAIT_OBJECT_0)
			result = ERROR_SUCCESS;
		else if (wait == WAIT_TIMEOUT)
			result = WAIT_TIMEOUT;
		else if (wait == WAIT_OBJECT_0 + 1)
		{
			if (!GetExitCodeProcess(process.hProcess, &result))
				result = GetLastError();
			else if (!result)
				result = ERROR_DLL_INIT_FAILED;
		}
		else
			result = GetLastError();
	}
	if (result != ERROR_SUCCESS)
	{
		TerminateProcess(process.hProcess, result);
		WaitForSingleObject(process.hProcess, 5000);
	}
	CloseHandle(process.hThread);
	CloseHandle(process.hProcess);
	CloseHandle(ready);
	if (result)
		return Fail(result);
	std::printf("READY %lu\n", process.dwProcessId);
	std::fflush(stdout);
	return 0;
}
