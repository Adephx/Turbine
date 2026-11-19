using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace Turbine;

public sealed class RuntimeDependencies(string root, HttpClient? http = null)
{
	private const int PackageLimit = 64 * 1024 * 1024;
	private const int LibraryLimit = 32 * 1024 * 1024;
	private const string ReceiptPath = "Dependencies/RuntimeFiles.json";
	private static readonly HttpClient Http = new()
	{
		Timeout = TimeSpan.FromMinutes(2)
	};
	private static readonly DependencyFile[] Files = ReadManifest();
	private readonly Dictionary<string, FileStamp> currentFiles = ReadReceipt(root);
	private bool receiptChanged;

	private sealed record FileStamp(string Hash, long Length, long LastWriteUtcTicks);

	private sealed record DependencyFile(string Name, string Package, string Version, string Asset, string Hash)
	{
		public string Component => Package is "SteamKit2" or "protobuf-net" or "protobuf-net.Core" or "ZstdSharp.Port" or "System.IO.Hashing" ? "SteamKit" : Package;

		public string RelativePath => Component + "/" + Name;

		public int SizeLimit => Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? LibraryLimit : 1024 * 1024;

		public string Path(string root) => Storage.Inside(root, "Dependencies/" + RelativePath);
	}

	private static DependencyFile[] ReadManifest()
	{
		using var stream = typeof(RuntimeDependencies).Assembly.GetManifestResourceStream("Turbine.RuntimeDependencies") ?? throw new IOException("The dependency manifest is missing.");
		using var reader = new StreamReader(stream);
		var files = new List<DependencyFile>();
		foreach (var line in reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var fields = line.Split('|');
			if (fields.Length != 5)
				throw new IOException("The dependency manifest is invalid.");
			files.Add(new DependencyFile(fields[0], fields[1], fields[2], fields[3], fields[4]));
		}

		return files.ToArray();
	}

	public static void Register(string root)
	{
		AssemblyLoadContext.Default.Resolving += (context, name) =>
		{
			var file = Files.FirstOrDefault(file => file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetFileNameWithoutExtension(file.Name).Equals(name.Name, StringComparison.OrdinalIgnoreCase));
			if (file == null || !File.Exists(file.Path(root)))
				return null;
			return context.LoadFromAssemblyPath(file.Path(root));
		};
	}

	private static Dictionary<string, FileStamp> ReadReceipt(string root)
	{
		try
		{
			return Storage.Read<Dictionary<string, FileStamp>>(Storage.Inside(root, ReceiptPath));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private void SaveReceipt()
	{
		if (!receiptChanged)
			return;
		try
		{
			Storage.Write(Storage.Inside(root, ReceiptPath), currentFiles);
			receiptChanged = false;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Verified dependencies can still run from a read-only folder.
		}
	}

	private void Remember(DependencyFile file, FileStamp stamp)
	{
		currentFiles[file.RelativePath] = stamp;
		receiptChanged = true;
	}

	private async Task<bool> IsCurrent(DependencyFile file, CancellationToken cancel)
	{
		var target = new FileInfo(file.Path(root));
		if (!target.Exists || target.Length > file.SizeLimit)
			return false;
		var stamp = new FileStamp(file.Hash, target.Length, target.LastWriteTimeUtc.Ticks);
		if (currentFiles.TryGetValue(file.RelativePath, out var current) && current == stamp)
			return true;
		await using var existing = target.OpenRead();
		if (Convert.ToHexString(await SHA256.HashDataAsync(existing, cancel).ConfigureAwait(false)) != file.Hash)
			return false;
		Remember(file, stamp);
		return true;
	}

	public async Task<bool> NeedsInstall(CancellationToken cancel = default)
	{
		try
		{
			foreach (var file in Files)
			{
				cancel.ThrowIfCancellationRequested();
				if (!await IsCurrent(file, cancel).ConfigureAwait(false))
					return true;
			}
			return false;
		}
		finally
		{
			SaveReceipt();
		}
	}

	public async Task Install(CancellationToken cancel = default, Action? downloadStarting = null)
	{
		foreach (var group in Files.GroupBy(file => (file.Package, file.Version)))
		{
			MemoryStream? package = null;
			ZipArchive? archive = null;
			try
			{
				foreach (var file in group)
				{
					cancel.ThrowIfCancellationRequested();
					if (await IsCurrent(file, cancel).ConfigureAwait(false))
						continue;
					downloadStarting?.Invoke();
					var limit = file.SizeLimit;
					byte[] bytes;
					if (file.Asset.StartsWith("https://", StringComparison.Ordinal))
					{
						using var output = await Download(file.Asset, limit, cancel).ConfigureAwait(false);
						bytes = output.ToArray();
					}
					else
					{
						if (archive == null)
						{
							var id = file.Package.ToLowerInvariant();
							var version = file.Version.ToLowerInvariant();
							package = await Download($"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg", PackageLimit, cancel).ConfigureAwait(false);
							archive = new ZipArchive(package, ZipArchiveMode.Read);
						}
						var entry = archive.GetEntry(file.Asset) ?? throw new IOException(file.Name + " is missing from the package.");
						if (entry.Length > limit)
							throw new IOException(file.Name + " is too large.");
						await using var content = entry.Open();
						using var output = await ReadBounded(content, limit, cancel).ConfigureAwait(false);
						bytes = output.ToArray();
					}
					if (Convert.ToHexString(SHA256.HashData(bytes)) != file.Hash)
						throw new IOException(file.Name + " checksum does not match.");
					cancel.ThrowIfCancellationRequested();
					var target = file.Path(root);
					Storage.WriteBytes(target, bytes);
					Remember(file, new FileStamp(file.Hash, bytes.LongLength, File.GetLastWriteTimeUtc(target).Ticks));
				}
			}
			catch (OperationCanceledException) when (cancel.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException)
			{
				throw new IOException(group.Key.Package + " download failed. Restart Turbine to retry.\n" + ex.Message, ex);
			}
			finally
			{
				archive?.Dispose();
				package?.Dispose();
				SaveReceipt();
			}
		}
	}

	private async Task<MemoryStream> Download(string url, int limit, CancellationToken cancel)
	{
		var client = http ?? Http;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		timeout.CancelAfter(client.Timeout);
		using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength > limit)
			throw new IOException("The download exceeds its size limit.");
		await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
		return await ReadBounded(input, limit, timeout.Token).ConfigureAwait(false);
	}

	private static async Task<MemoryStream> ReadBounded(Stream input, int limit, CancellationToken cancel)
	{
		var output = new MemoryStream();
		try
		{
			var buffer = new byte[81920];
			int read;
			while ((read = await input.ReadAsync(buffer, cancel).ConfigureAwait(false)) != 0)
			{
				if (output.Length + read > limit)
					throw new IOException("The download exceeds its size limit.");
				await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
			}

			output.Position = 0;
			return output;
		}
		catch
		{
			output.Dispose();
			throw;
		}
	}
}
