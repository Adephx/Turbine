using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;

namespace Turbine;

public sealed class TemporaryDefenderExclusion : IAsyncDisposable
{
	private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromMinutes(5);
	private static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(1);
	private readonly Process? helper;
	private readonly NamedPipeServerStream? pipe;
	private readonly StreamReader? reader;
	private readonly StreamWriter? writer;
	private int disposed;

	private TemporaryDefenderExclusion(Process? helper, NamedPipeServerStream? pipe, StreamReader? reader, StreamWriter? writer)
	{
		this.helper = helper;
		this.pipe = pipe;
		this.reader = reader;
		this.writer = writer;
	}

	public static async Task<TemporaryDefenderExclusion> Create(string stagingDirectory, CancellationToken cancel)
	{
		cancel.ThrowIfCancellationRequested();
		var staging = ValidateStagingDirectory(stagingDirectory);
		var powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
		if (!File.Exists(powerShell))
			throw new IOException("Windows Defender PowerShell is unavailable.");
		var pipeName = "Turbine-Defender-" + Guid.NewGuid().ToString("N");
		var taskName = "Turbine-Defender-" + Path.GetFileName(staging)[8..];
		using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		handshake.CancelAfter(HandshakeTimeout);
		var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		var start = new ProcessStartInfo(powerShell)
		{
			UseShellExecute = true,
			Verb = "runas",
			WindowStyle = ProcessWindowStyle.Hidden
		};
		foreach (var arg in new[]
		{
			"-NoProfile",
			"-NonInteractive",
			"-WindowStyle",
			"Hidden",
			"-Command",
			BuildHelperScript(staging, powerShell, taskName, pipeName)
		})
			start.ArgumentList.Add(arg);
		Process? process = null;
		try
		{
			process = Process.Start(start) ?? throw new IOException("Could not start the Defender helper.");
			var connecting = pipe.WaitForConnectionAsync(handshake.Token);
			var exiting = process.WaitForExitAsync(handshake.Token);
			if (await Task.WhenAny(connecting, exiting).ConfigureAwait(false) == exiting && !connecting.IsCompletedSuccessfully)
				throw new IOException("Defender helper exited before connecting.");
			await connecting.ConfigureAwait(false);
			var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
			var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
			{
				AutoFlush = true
			};
			var result = await reader.ReadLineAsync(handshake.Token).ConfigureAwait(false);
			if (result == "added")
				return new TemporaryDefenderExclusion(process, pipe, reader, writer);
			if (result == "existing")
			{
				await process.WaitForExitAsync(handshake.Token).ConfigureAwait(false);
				reader.Dispose();
				writer.Dispose();
				pipe.Dispose();
				process.Dispose();
				return new TemporaryDefenderExclusion(null, null, null, null);
			}

			throw new IOException(result?.StartsWith("error:", StringComparison.Ordinal) == true ? result[6..] : "Defender did not confirm the exclusion.");
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			pipe.Dispose();
			throw new IOException("Administrator approval was declined.", ex);
		}
		catch
		{
			pipe.Dispose();
			if (process != null)
			{
				using var cleanup = new CancellationTokenSource(CleanupTimeout);
				try
				{
					await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
				}
				finally
				{
					process.Dispose();
				}
			}

			throw;
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0 || helper is null)
			return;
		try
		{
			using var timeout = new CancellationTokenSource(CleanupTimeout);
			try
			{
				await writer!.WriteLineAsync("done").ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
			}

			var result = await reader!.ReadLineAsync(timeout.Token).ConfigureAwait(false);
			await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			if (result != "removed")
				throw new IOException(result?.StartsWith("error:", StringComparison.Ordinal) == true ? result[6..] : "Defender did not confirm exclusion cleanup; recovery remains scheduled.");
		}
		catch (OperationCanceledException ex)
		{
			throw new IOException("Defender exclusion cleanup timed out; recovery remains scheduled.", ex);
		}
		catch (IOException ex) when (!ex.Message.Contains("recovery remains scheduled", StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException("Defender exclusion cleanup could not be confirmed; recovery remains scheduled.", ex);
		}
		finally
		{
			try
			{
				writer?.Dispose();
			}
			catch (IOException)
			{
			}

			try
			{
				reader?.Dispose();
			}
			catch (IOException)
			{
			}

			pipe?.Dispose();
			helper.Dispose();
		}
	}

	private static string ValidateStagingDirectory(string input)
	{
		if (string.IsNullOrWhiteSpace(input) || !Path.IsPathFullyQualified(input) || input.IndexOfAny(['*', '?', '[', ']']) >= 0)
			throw new IOException("Invalid Defender staging folder.");
		var path = Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar);
		if (!input.TrimEnd(Path.DirectorySeparatorChar).Equals(path, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(path), @"^\.update-[0-9a-f]{32}$", RegexOptions.IgnoreCase) || !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "Dependencies", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path))
			throw new IOException("Invalid Defender staging folder.");
		for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
			if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
				throw new IOException("Linked folders cannot be excluded.");
		return path;
	}

	private static string QuotePowerShellString(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

	private static string BuildHelperScript(string staging, string powerShell, string taskName, string pipeName)
	{
		var path = QuotePowerShellString(staging);
		var task = QuotePowerShellString(taskName);
		var ps = QuotePowerShellString(powerShell);
		var name = QuotePowerShellString(pipeName);
		var modules = QuotePowerShellString(Path.Combine(Path.GetDirectoryName(powerShell)!, "Modules"));
		var visible = "$policy=Get-ItemProperty -LiteralPath 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows Defender' -ErrorAction SilentlyContinue;" + "$features=Get-ItemProperty -LiteralPath 'HKLM:\\SOFTWARE\\Microsoft\\Windows Defender\\Features' -Name TPExclusions -ErrorAction SilentlyContinue;" + "$managed=Get-ItemProperty -LiteralPath 'HKLM:\\SOFTWARE\\Microsoft\\Windows Defender' -Name ManagedDefenderProductType -ErrorAction SilentlyContinue;" + "if($policy.DisableLocalAdminMerge -eq 1 -or $policy.HideExclusionsFromLocalAdmins -eq 1 -or $features.TPExclusions -eq 1 -or $managed.ManagedDefenderProductType -in 6,7){throw 'Defender exclusions are managed'};";
		var cleanup = "$env:PSModulePath=" + modules + ";$ErrorActionPreference='Stop';$p=" + path + ";$t=" + task + ";" + "function HasPath { @((Get-MpPreference).ExclusionPath) -contains $p };" + "function VerifyVisible { " + visible + " };" + "try{Remove-MpPreference -ExclusionPath $p -ErrorAction Stop}catch{};" + "VerifyVisible;if(HasPath){throw 'Defender exclusion remains'};" + "Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue;" + "if(Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue){throw 'Recovery task remains'}";
		return "$env:PSModulePath=" + modules + ";$ErrorActionPreference='Stop';$p=" + path + ";$t=" + task + ";$ps=" + ps + ";$n=" + name + ";$registered=$false;$failure='';$pipe=$null;$reader=$null;$writer=$null;" + "function HasPath { @((Get-MpPreference).ExclusionPath) -contains $p };" + "function VerifyVisible { " + visible + " };" + "try{" + "$pipe=[IO.Pipes.NamedPipeClientStream]::new('.', $n, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous, [Security.Principal.TokenImpersonationLevel]::Anonymous);" + "$pipe.Connect(30000);$reader=[IO.StreamReader]::new($pipe);$writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true;" + "$status=Get-MpComputerStatus;" + "if(-not($status.AMServiceEnabled -and $status.AntivirusEnabled -and $status.RealTimeProtectionEnabled)){throw 'Microsoft Defender is inactive'};" + "VerifyVisible;" + "$node=Get-Item -LiteralPath $p -Force;while($null -ne $node){if($node.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked folder'};$node=$node.Parent};" + "if(HasPath){$writer.WriteLine('existing')}else{" + "$action=New-ScheduledTaskAction -Execute $ps -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -Command '+[char]34+" + QuotePowerShellString(cleanup) + "+[char]34);" + "$triggers=@((New-ScheduledTaskTrigger -AtStartup),(New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(10)));" + "$settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5);" + "Register-ScheduledTask -TaskName $t -Action $action -Trigger $triggers -Settings $settings -User 'SYSTEM' -RunLevel Highest | Out-Null;" + "$registered=$true;Add-MpPreference -ExclusionPath $p -ErrorAction Stop;" + "if(-not(HasPath)){throw 'Defender rejected the exclusion'};" + "$writer.WriteLine('added');$read=$reader.ReadLineAsync();[void]$read.Wait(600000)" + "}}catch{$failure=$_.Exception.Message}finally{" + "if($registered){try{try{Remove-MpPreference -ExclusionPath $p -ErrorAction Stop}catch{};" + "VerifyVisible;if(HasPath){throw 'Defender exclusion remains'};" + "Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue;" + "if(Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue){throw 'Recovery task remains'}" + "}catch{$failure='Could not confirm Defender exclusion removal; recovery remains scheduled.'}};" + "if($writer){try{if($failure){$writer.WriteLine('error:'+$failure)}elseif($registered){$writer.WriteLine('removed')}}catch{}};" + "if($writer){try{$writer.Dispose()}catch{}};if($reader){try{$reader.Dispose()}catch{}};if($pipe){try{$pipe.Dispose()}catch{}}" + "}";
	}
}
