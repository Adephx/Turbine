using System.IO;
using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace Turbine;

public static class SteamStub
{
	public static bool NeedsUnpacking(string file)
	{
		try
		{
			using var stream = File.OpenRead(file);
			using var pe = new PEReader(stream);
			var header = pe.PEHeaders.PEHeader;
			var bind = pe.PEHeaders.SectionHeaders.Where(s => s.Name == ".bind").ToArray();
			if (header == null || bind.Length == 0)
				return false;

			bool InBind(ulong rva) => bind.Any(s => rva >= (uint)s.VirtualAddress && rva - (uint)s.VirtualAddress < (uint)Math.Max(s.VirtualSize, s.SizeOfRawData));

			if (InBind((uint)header.AddressOfEntryPoint))
				return true;
			var tls = header.ThreadLocalStorageTableDirectory;
			if (tls.RelativeVirtualAddress == 0)
				return false;
			var x64 = header.Magic == PEMagic.PE32Plus;
			var directory = pe.GetSectionData(tls.RelativeVirtualAddress).GetReader();
			directory.Offset = x64 ? 24 : 12;
			var address = x64 ? directory.ReadUInt64() : directory.ReadUInt32();
			if (address == 0)
				return false;
			if (address < header.ImageBase || address - header.ImageBase > int.MaxValue)
				throw new BadImageFormatException("Invalid TLS callback address.");
			var callbacks = pe.GetSectionData((int)(address - header.ImageBase)).GetReader();
			while (callbacks.RemainingBytes >= (x64 ? 8 : 4))
			{
				var callback = x64 ? callbacks.ReadUInt64() : callbacks.ReadUInt32();
				if (callback == 0)
					return false;
				if (callback >= header.ImageBase && InBind(callback - header.ImageBase))
					return true;
			}

			throw new BadImageFormatException("Unterminated TLS callbacks.");
		}
		catch (BadImageFormatException ex)
		{
			throw new IOException($"Cannot inspect executable {Path.GetFileName(file)}: {ex.Message}", ex);
		}
	}

	public static byte[] Unpack(Library library, Game game, string executable)
	{
		var directory = Storage.Inside(library.Root, "Dependencies/Steamless");
		var cli = Storage.Inside(directory, "Steamless.CLI.exe");
		if (!File.Exists(cli))
			throw new IOException("The Steamless dependency is missing. Restart Turbine to retry its download.");
		var stagingName = ".unpack-" + Guid.NewGuid().ToString("N");
		var staging = library.SettingsFile(game, stagingName);
		Directory.CreateDirectory(staging);
		var copy = Storage.Inside(staging, Path.GetFileName(executable));
		try
		{
			File.Copy(executable, copy);
			var start = new ProcessStartInfo(cli)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = staging,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			start.ArgumentList.Add("--quiet");
			start.ArgumentList.Add("--keepstub");
			start.ArgumentList.Add(copy);
			using var process = Process.Start(start) ?? throw new IOException("Could not start Steamless.");
			var output = process.StandardOutput.ReadToEndAsync();
			var error = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(120_000))
			{
				process.Kill(entireProcessTree: true);
				process.WaitForExit();
				throw new IOException("SteamStub unpacking timed out. The original executable was not changed.");
			}

			Task.WhenAll(output, error).GetAwaiter().GetResult();
			var result = copy + ".unpacked.exe";
			if (process.ExitCode != 0 || !File.Exists(result))
				throw new IOException($"Steamless could not unpack {Path.GetFileName(executable)}. Its SteamStub variant may be unsupported. No game files were changed.");
			if (Emulator.Architecture(result) != Emulator.Architecture(executable))
				throw new IOException("SteamStub produced an incompatible executable. The original was not changed.");
			return File.ReadAllBytes(result);
		}
		finally
		{
			Storage.DeleteTree(library.SettingsPath(game), stagingName);
		}
	}
}
