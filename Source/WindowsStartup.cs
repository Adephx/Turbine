using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Turbine;

internal static class WindowsStartup
{
	internal const string Argument = "--startup";
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "Turbine";
	private const int CommandLengthLimit = 260;

	private static string? Command =>
		Environment.ProcessPath is { } executable ? $"\"{executable}\" {Argument}" : null;

	internal static bool IsEnabled()
	{
		var command = Command;
		if (command == null)
			return false;

		try
		{
			using var key = Registry.CurrentUser.OpenSubKey(RunKey);
			return MatchesCommand(key, command);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
		{
			return false;
		}
	}

	internal static void SetEnabled(bool enabled)
	{
		var command = Command ?? throw new InvalidOperationException("Could not locate Turbine.exe.");
		if (enabled && command.Length > CommandLengthLimit)
			throw new InvalidOperationException("Turbine's path is too long for Windows startup.");

		try
		{
			if (enabled)
			{
				using var key = Registry.CurrentUser.CreateSubKey(RunKey);
				key.SetValue(ValueName, command, RegistryValueKind.String);
				return;
			}

			using var existingKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
			if (existingKey != null && MatchesCommand(existingKey, command))
				existingKey.DeleteValue(ValueName, throwOnMissingValue: false);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
		{
			throw new InvalidOperationException("Could not update Windows startup.", ex);
		}
	}

	private static bool MatchesCommand(RegistryKey? key, string command)
	{
		var existing = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
		return string.Equals(existing, command, StringComparison.OrdinalIgnoreCase);
	}
}
