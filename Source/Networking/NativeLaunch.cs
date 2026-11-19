using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Turbine.Networking;

internal static class NativeLaunch
{
	private static readonly ConcurrentDictionary<string, byte[]> HelperDigests = new(StringComparer.Ordinal);

	public static async Task<Process> StartAsync(ProcessStartInfo game, NetworkBroker.GameClient client, CancellationToken cancellationToken)
	{
		if (game.UseShellExecute || game.RedirectStandardInput || game.RedirectStandardOutput || game.RedirectStandardError)
			throw new InvalidOperationException("Games must launch directly without redirected input or output.");
		string executable = Path.GetFullPath(game.FileName);
		string architecture = ReadArchitecture(executable);
		string x86 = await ExtractAsync("Turbine.Hook.x86.dll", cancellationToken).ConfigureAwait(false);
		string x64 = await ExtractAsync("Turbine.Hook.x64.dll", cancellationToken).ConfigureAwait(false);
		string dll = architecture == "x86" ? x86 : x64;
		string launcher = await ExtractAsync($"Turbine.Launcher.{architecture}.exe", cancellationToken).ConfigureAwait(false);
		string command = Quote(executable);
		if (game.ArgumentList.Count > 0)
			command += " " + string.Join(" ", game.ArgumentList.Select(Quote));
		else if (!string.IsNullOrWhiteSpace(game.Arguments))
			command += " " + game.Arguments;
		var start = new ProcessStartInfo(launcher)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			WorkingDirectory = string.IsNullOrEmpty(game.WorkingDirectory) ? Path.GetDirectoryName(executable)! : game.WorkingDirectory
		};
		foreach (var item in game.Environment)
			start.Environment[item.Key] = item.Value;
		start.Environment["TURBINE_PIPE"] = client.PipeName;
		start.Environment["TURBINE_TOKEN"] = Convert.ToHexString(client.Token);
		start.Environment["TURBINE_NATIVE"] = dll;
		start.Environment["TURBINE_NATIVE_X86"] = x86;
		start.Environment["TURBINE_NATIVE_X64"] = x64;
		foreach (var value in new[]
		{
			"--dll",
			dll,
			"--app",
			executable,
			"--cwd",
			start.WorkingDirectory,
			"--command-line",
			command
		})
			start.ArgumentList.Add(value);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(45));
		using var helper = Process.Start(start) ?? throw new IOException("The networking launcher could not start.");
		Process? process = null;
		try
		{
			string? line = await helper.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
			if (line is null || !line.StartsWith("PID ", StringComparison.Ordinal) || !int.TryParse(line.AsSpan(4), out int pid) || pid <= 0)
				throw new IOException("The networking launcher could not create the game. " + line);
			process = Process.GetProcessById(pid);
			_ = process.Handle;
			client.Authorize(pid);
			await helper.StandardInput.WriteLineAsync("GO".AsMemory(), timeout.Token).ConfigureAwait(false);
			await helper.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
			line = await helper.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
			if (line != $"READY {pid}")
				throw new IOException("Game networking initialization failed. " + line);
			await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			if (helper.ExitCode != 0 || !client.HandshakeSucceeded)
				throw new IOException("Game networking initialization failed.");
			return process;
		}
		catch
		{
			try
			{
				if (process is { HasExited: false })
					process.Kill(true);
			}
			catch (Exception)
			{
			}

			process?.Dispose();
			try
			{
				if (!helper.HasExited)
					helper.Kill(true);
			}
			catch (Exception)
			{
			}

			throw;
		}
	}

	private static string ReadArchitecture(string path)
	{
		using var stream = File.OpenRead(path);
		using var reader = new BinaryReader(stream);
		if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d)
			throw new IOException("Select a Windows game executable.");
		stream.Position = 0x3c;
		int offset = reader.ReadInt32();
		if (offset < 64 || offset > stream.Length - 6)
			throw new IOException("The executable header is invalid.");
		stream.Position = offset;
		if (reader.ReadUInt32() != 0x00004550)
			throw new IOException("The executable header is invalid.");
		return reader.ReadUInt16() switch
		{
			0x014c => "x86",
			0x8664 => "x64",
			_ => throw new NotSupportedException("Game networking supports x86 and x64 games.")
		};
	}

	private static async Task<string> ExtractAsync(string name, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		byte[] digest = HelperDigests.GetOrAdd(name, static helper =>
		{
			using var source = OpenHelperResource(helper);
			return SHA256.HashData(source);
		});
		string hash = Convert.ToHexString(digest);
		string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Turbine", "Runtime", hash);
		string target = Path.Combine(directory, name);
		for (var attempt = 0; ; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				if (File.Exists(target))
				{
					await using var existing = File.OpenRead(target);
					if (CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false), digest))
						return target;
				}

				await InstallHelperAsync(name, directory, target, cancellationToken).ConfigureAwait(false);
				return target;
			}
			catch (Exception error) when (attempt < 4 && (error is IOException or UnauthorizedAccessException) &&
				(error.HResult & 0xFFFF) is 5 or 32 && File.Exists(target))
			{
				// A parallel copy may be reading or replacing the same helper.
				await Task.Delay(25, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private static async Task InstallHelperAsync(string name, string directory, string target, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(directory);
		string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			await using var source = OpenHelperResource(name);
			await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
				await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporary, target, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	private static Stream OpenHelperResource(string name) =>
		typeof(NativeLaunch).Assembly.GetManifestResourceStream("Turbine." + name) ?? throw new IOException("The networking component is missing: " + name);

	private static string Quote(string value)
	{
		var result = new StringBuilder("\"");
		int slashes = 0;
		foreach (char character in value)
		{
			if (character == '\\')
			{
				slashes++;
				continue;
			}

			if (character == '"')
				result.Append('\\', slashes * 2 + 1).Append('"');
			else
				result.Append('\\', slashes).Append(character);
			slashes = 0;
		}

		return result.Append('\\', slashes * 2).Append('"').ToString();
	}
}
