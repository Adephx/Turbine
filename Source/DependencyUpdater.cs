using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Archives;

namespace Turbine;

public sealed class DependencyUpdater(string root)
{
	private const long SizeLimit = 128 * 1024 * 1024;
	private static readonly HttpClient Http = new()
	{
		Timeout = TimeSpan.FromMinutes(5)
	};
	private static readonly SemaphoreSlim Gate = new(1, 1);
	private static readonly string[] SteamlessFiles = [
		"Steamless.CLI.exe",
		"Steamless.CLI.exe.config",
		"Plugins/Steamless.API.dll",
		"Plugins/SharpDisasm.dll",
		"Plugins/Steamless.Unpacker.Variant10.x86.dll",
		"Plugins/Steamless.Unpacker.Variant20.x86.dll",
		"Plugins/Steamless.Unpacker.Variant21.x86.dll",
		"Plugins/Steamless.Unpacker.Variant30.x86.dll",
		"Plugins/Steamless.Unpacker.Variant30.x64.dll",
		"Plugins/Steamless.Unpacker.Variant31.x86.dll",
		"Plugins/Steamless.Unpacker.Variant31.x64.dll"
	];
	private static readonly Dictionary<string, string> GoldbergFiles = new()
	{
		["experimental/x86/steam_api.dll"] = "x86/steam_api.dll",
		["experimental/x64/steam_api64.dll"] = "x64/steam_api64.dll"
	};

	public IReadOnlyList<string> MissingComponents()
	{
		var missing = new List<string>();
		if (GoldbergFiles.Values.Any(file => !File.Exists(Storage.Inside(root, "Dependencies/Goldberg/" + file))))
			missing.Add("Goldberg");
		if (SteamlessFiles.Any(file => !File.Exists(Storage.Inside(root, "Dependencies/Steamless/" + file))))
			missing.Add("Steamless");
		return missing;
	}

	public async Task Update(CancellationToken cancel = default, Func<string, Task<bool>>? confirmExclusion = null, Action? downloadStarting = null, IReadOnlyList<string>? components = null)
	{
		if (!await Gate.WaitAsync(0, cancel).ConfigureAwait(false))
			throw new IOException("A dependency update is already running.");
		try
		{
			var errors = new List<string>();
			foreach (var component in components ?? ["Steamless", "Goldberg"])
			{
				try
				{
					await UpdateComponent(
						component,
						component == "Steamless" ? "atom0s/Steamless" : "Detanup01/gbe_fork",
						component == "Steamless" ? SteamlessFiles.ToDictionary(x => x) : GoldbergFiles,
						confirmExclusion,
						cancel,
						downloadStarting
					).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
				{
					errors.Add(component + " update timed out.");
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					errors.Add(component + ": " + ex.Message);
				}
			}

			if (errors.Count > 0)
				throw new IOException(string.Join("\n", errors));
		}
		finally
		{
			Gate.Release();
		}
	}

	private async Task UpdateComponent(
		string component,
		string repository,
		Dictionary<string, string> files,
		Func<string, Task<bool>>? confirmExclusion,
		CancellationToken cancel,
		Action? downloadStarting
	)
	{
		var dependencies = Storage.Inside(root, "Dependencies");
		Directory.CreateDirectory(dependencies);
		var installed = Storage.Inside(dependencies, component);
		var manifestPath = Storage.Inside(installed, "Release.json");
		Release? previous = null;
		if (File.Exists(manifestPath))
		{
			try
			{
				previous = Storage.Read<Release>(manifestPath);
			}
			catch (IOException)
			{
			}
		}

		using var request = CreateRequest("https://api.github.com/repos/" + repository + "/releases/latest");
		if (previous?.Repository == repository && !string.IsNullOrWhiteSpace(previous.ETag))
			request.Headers.TryAddWithoutValidation("If-None-Match", previous.ETag);
		using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		checkTimeout.CancelAfter(TimeSpan.FromSeconds(20));
		using var response = await Http.SendAsync(request, checkTimeout.Token).ConfigureAwait(false);
		Release release;
		if (response.StatusCode == HttpStatusCode.NotModified && previous != null)
			release = previous;
		else
		{
			if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
				throw new IOException("GitHub's download limit was reached. Try Refresh later.");
			response.EnsureSuccessStatusCode();
			using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(checkTimeout.Token).ConfigureAwait(false));
			var json = data.RootElement;
			if (json.GetProperty("draft").GetBoolean() || json.GetProperty("prerelease").GetBoolean())
				throw new IOException("GitHub did not return a stable published release.");
			var asset = SelectAsset(json.GetProperty("assets"), component);
			release = new Release
			{
				Repository = repository,
				Tag = json.GetProperty("tag_name").GetString() ?? "",
				ETag = response.Headers.ETag?.ToString() ?? "",
				Asset = asset.GetProperty("name").GetString() ?? "",
				AssetId = asset.GetProperty("id").GetInt64(),
				DownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "",
				Size = asset.GetProperty("size").GetInt64(),
				Digest = asset.TryGetProperty("digest", out var digest) ? digest.GetString() ?? "" : ""
			};
		}

		ValidateUrl(release.DownloadUrl, "github.com", "/" + repository + "/releases/download/");
		if (string.IsNullOrWhiteSpace(release.Tag) || release.Size <= 0 || release.Size > SizeLimit)
			throw new IOException("The published dependency metadata is invalid.");
		if (previous?.Tag == release.Tag && previous.Asset == release.Asset && previous.AssetId == release.AssetId && previous.Size == release.Size && previous.Digest == release.Digest && files.Values.All(previous.Files.ContainsKey) && await InstalledFilesMatch(installed, previous.Files, cancel).ConfigureAwait(false))
		{
			if (previous.ETag != release.ETag)
			{
				previous.ETag = release.ETag;
				Storage.Write(manifestPath, previous);
			}
			return;
		}

		var stagingName = ".update-" + Guid.NewGuid().ToString("N");
		var staging = Storage.Inside(dependencies, stagingName);
		var ready = Storage.Inside(staging, "ready");
		var backup = Storage.Inside(staging, "previous");
		var keepBackup = false;
		Directory.CreateDirectory(ready);

		async Task InstallComponent()
		{
			downloadStarting?.Invoke();
			var archivePath = Storage.Inside(staging, release.Asset);
			await Download(release.DownloadUrl, archivePath, release.Size, cancel).ConfigureAwait(false);
			await using (var archiveFile = File.OpenRead(archivePath))
			{
				var actual = Convert.ToHexString(await SHA256.HashDataAsync(archiveFile, cancel).ConfigureAwait(false));
				if (!string.IsNullOrEmpty(release.Digest) && !release.Digest.Equals("sha256:" + actual, StringComparison.OrdinalIgnoreCase))
					throw new IOException("The " + component + " download does not match GitHub's SHA256 digest.");
				release.ArchiveSha256 = actual;
			}

			await Task.Run(() => ExtractRuntimeFiles(archivePath, ready, files, cancel), cancel).ConfigureAwait(false);
			var tag = Uri.EscapeDataString(release.Tag);
			await Download(
				"https://raw.githubusercontent.com/" + repository + "/" + tag + "/LICENSE",
				Storage.Inside(ready, "LICENSE.txt"),
				null,
				cancel
			).ConfigureAwait(false);
			if (component == "Goldberg")
				await Download(
					"https://raw.githubusercontent.com/" + repository + "/" + tag + "/CREDITS.md",
					Storage.Inside(ready, "CREDITS.md"),
					null,
					cancel
				).ConfigureAwait(false);
			release.SourceUrl = "https://github.com/" + repository + "/tree/" + tag;
			release.Files.Clear();
			foreach (var file in Storage.Files(ready))
			{
				await using var input = File.OpenRead(file);
				release.Files[Path.GetRelativePath(ready, file).Replace('\\', '/')] = Convert.ToHexString(await SHA256.HashDataAsync(input, cancel).ConfigureAwait(false));
			}

			Storage.Write(Storage.Inside(ready, "Release.json"), release);
			cancel.ThrowIfCancellationRequested();
			var hadPrevious = Directory.Exists(installed);
			if (hadPrevious)
			{
				Storage.Files(installed);
				Directory.Move(installed, backup);
			}

			try
			{
				Directory.Move(ready, installed);
			}
			catch (Exception installError) when (hadPrevious)
			{
				try
				{
					Directory.Move(backup, installed);
				}
				catch (Exception restoreError)
				{
					keepBackup = true;
					throw new IOException($"Could not restore the previous {component} installation. Its files are saved in {backup}.", new AggregateException(installError, restoreError));
				}

				throw;
			}
		}

		try
		{
			try
			{
				await InstallComponent().ConfigureAwait(false);
			}
			catch (IOException ex) when ((ex.HResult & 0xFFFF) is 225 or 226 && confirmExclusion != null)
			{
				if (!System.Text.RegularExpressions.Regex.IsMatch(release.Digest, "^sha256:[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
					throw new IOException("Blocked download has no published checksum.", ex);
				if (!await confirmExclusion(component).ConfigureAwait(false))
					return;
				cancel.ThrowIfCancellationRequested();
				await using var exclusion = await TemporaryDefenderExclusion.Create(staging, cancel).ConfigureAwait(false);
				await InstallComponent().ConfigureAwait(false);
			}
		}
		catch (IOException ex) when ((ex.HResult & 0xFFFF) is 225 or 226 || ex is FileNotFoundException)
		{
			throw new IOException("Download blocked or removed. Check your antivirus.", ex);
		}
		finally
		{
			if (!keepBackup)
				Storage.DeleteTree(dependencies, stagingName);
		}
	}

	private static JsonElement SelectAsset(JsonElement assets, string component)
	{
		var matching = assets.EnumerateArray().Where(asset =>
		{
			var name = asset.GetProperty("name").GetString() ?? "";
			return component == "Goldberg" ? name.Equals("emu-win-release-vs26.7z", StringComparison.OrdinalIgnoreCase) : name.StartsWith("Steamless.v", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
		}).ToArray();
		if (matching.Length != 1)
			throw new IOException("The latest " + component + " release has no unambiguous supported Windows archive.");
		return matching[0];
	}

	private static HttpRequestMessage CreateRequest(string url)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.UserAgent.ParseAdd("Turbine/0.1.0");
		return request;
	}

	private static void ValidateUrl(string url, string host, string pathPrefix)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != host || !uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal) || uri.UserInfo.Length != 0)
			throw new IOException("The dependency URL is not an official upstream URL.");
	}

	private static async Task Download(string url, string target, long? expectedSize, CancellationToken cancel)
	{
		using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		downloadTimeout.CancelAfter(Http.Timeout);
		var downloadToken = downloadTimeout.Token;
		using var request = CreateRequest(url);
		using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, downloadToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength > SizeLimit)
			throw new IOException("The dependency download is too large.");
		await using var input = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false);
		await using var output = File.Create(target);
		var size = 0L;
		var buffer = new byte[81920];
		int count;
		while ((count = await input.ReadAsync(buffer, downloadToken).ConfigureAwait(false)) != 0)
		{
			size += count;
			if (size > (expectedSize ?? SizeLimit))
				throw new IOException("The dependency download exceeds its expected size.");
			await output.WriteAsync(buffer.AsMemory(0, count), downloadToken).ConfigureAwait(false);
		}

		if (expectedSize.HasValue && size != expectedSize.Value)
			throw new IOException("The dependency download is incomplete.");
	}

	private static void ExtractRuntimeFiles(string archivePath, string destination, Dictionary<string, string> files, CancellationToken cancel)
	{
		using var archive = ArchiveFactory.OpenArchive(archivePath);
		var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in archive.Entries)
		{
			cancel.ThrowIfCancellationRequested();
			if (entry.IsDirectory)
				continue;
			var key = (entry.Key ?? "").Replace('\\', '/');
			var match = files.Keys.FirstOrDefault(path => key.Equals(path, StringComparison.OrdinalIgnoreCase) || key.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase));
			if (match == null)
				continue;
			if (!selected.Add(match) || entry.Size < 0 || entry.Size > SizeLimit)
				throw new IOException("The dependency archive contains invalid or duplicate runtime files.");
			var target = Storage.Inside(destination, files[match]);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			using var input = entry.OpenEntryStream();
			using var output = File.Create(target);
			var buffer = new byte[81920];
			var length = 0L;
			int count;
			while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
			{
				cancel.ThrowIfCancellationRequested();
				length += count;
				if (length > SizeLimit || length > entry.Size)
					throw new IOException("The dependency archive contains an oversized file.");
				output.Write(buffer, 0, count);
			}

			if (length != entry.Size)
				throw new IOException("The dependency archive contains an incomplete file.");
		}

		if (selected.Count != files.Count)
			throw new IOException("The published dependency archive is missing required runtime files.");
	}

	private static async Task<bool> InstalledFilesMatch(string directory, Dictionary<string, string> files, CancellationToken cancel)
	{
		foreach (var (relative, hash) in files)
		{
			var path = Storage.Inside(directory, relative);
			if (!File.Exists(path))
				return false;
			await using var input = File.OpenRead(path);
			if (!hash.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input, cancel).ConfigureAwait(false)), StringComparison.OrdinalIgnoreCase))
				return false;
		}

		return true;
	}

	public sealed class Release
	{
		public string Repository { get; set; } = "";

		public string Tag { get; set; } = "";

		public string ETag { get; set; } = "";

		public string Asset { get; set; } = "";

		public long AssetId
		{
			get; set;
		}

		public string DownloadUrl { get; set; } = "";

		public long Size
		{
			get; set;
		}

		public string Digest { get; set; } = "";

		public string ArchiveSha256 { get; set; } = "";

		public string SourceUrl { get; set; } = "";

		public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	}
}
