using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamKit2;

namespace Turbine;

public sealed record SteamMatch(uint AppId, string Name)
{
	public override string ToString() => $"{Name} · {AppId}";
}

public sealed class SteamMetadata
{
	public uint AppId
	{
		get; set;
	}

	public string Name { get; set; } = "";

	public string Source { get; set; } = "";

	public DateTimeOffset RetrievedAt
	{
		get; set;
	}

	public Dictionary<uint, string> Dlcs { get; set; } = [];

	public List<string> Executables { get; set; } = [];
}

public sealed record DiscoveryResult(string Message, IReadOnlyList<SteamMatch>? Matches = null);

public sealed class SteamDiscovery(HttpClient? http = null)
{
	private static readonly HttpClient SharedHttp = new()
	{
		Timeout = TimeSpan.FromSeconds(12)
	};
	private readonly HttpClient http = http ?? SharedHttp;

	public static string Normalize(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant();

	private static SteamMatch? FindExactMatch(IEnumerable<SteamMatch> matches, string name)
	{
		var wanted = Normalize(name);
		if (wanted.Length < 3)
			return null;
		var exact = matches.Where(match => wanted == Normalize(match.Name)).DistinctBy(match => match.AppId).ToArray();
		return exact.Length == 1 ? exact[0] : null;
	}

	private static string CleanName(string name) => Regex.Replace(name, @"[\p{Cc};#=]", " ").Trim();

	private static List<string> GetSearchNames(string root, Game game)
	{
		var names = new List<string>
		{
			game.Name,
			Path.GetFileName(root)
		};
		foreach (var file in game.DetectedExecutables.Where(file => File.Exists(Storage.Inside(root, file))).Take(6))
		{
			var product = FileVersionInfo.GetVersionInfo(Storage.Inside(root, file)).ProductName;
			if (!string.IsNullOrWhiteSpace(product) && !Regex.IsMatch(product, "(?i)(unreal|unity|bootstrap|launcher|microsoft)"))
				names.Add(product);
			var stem = Regex.Replace(Path.GetFileNameWithoutExtension(file), @"(?i)([-_ ]?(win64|win32|x64|x86|shipping))+$", "");
			if (stem.Length >= 4 && !Regex.IsMatch(stem, "(?i)^(game|start|launch|launcher|bootstrap)$"))
				names.Add(stem);
		}

		return names.Where(n => n.Length >= 3).DistinctBy(Normalize).Take(4).ToList();
	}

	public async Task<List<SteamMatch>> Search(string name, CancellationToken cancel = default)
	{
		using var json = await GetJson($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=US", cancel);
		if (!json.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
			throw new IOException("Steam search returned no usable response.");
		return items.EnumerateArray().Where(x => ReadJsonText(x, "type") == "app").Select(x => new SteamMatch(x.GetProperty("id").GetUInt32(), ReadJsonText(x, "name"))).Where(x => x.AppId != 0 && x.Name.Length > 0).DistinctBy(x => x.AppId).ToList();
	}

	public async Task<DiscoveryResult> Discover(Library library, Game game, CancellationToken cancel = default)
	{
		cancel.ThrowIfCancellationRequested();
		if (!game.NeedsSteamLookup)
			return new DiscoveryResult("");

		DiscoveryResult CompleteAttempt(DiscoveryResult result)
		{
			cancel.ThrowIfCancellationRequested();
			game.SteamLookupAppId = game.AppId;
			library.SaveGame(game);
			return result;
		}

		var cache = Storage.Read<SteamMetadata>(library.SettingsFile(game, "Steam.json"));
		if (game.AppId != 0 && cache.AppId == game.AppId && cache.RetrievedAt != default)
		{
			cancel.ThrowIfCancellationRequested();
			game.SteamDataAppId = game.AppId;
			return CompleteAttempt(new DiscoveryResult(""));
		}

		var discoveredId = game.AppId;
		SteamMetadata metadata;
		try
		{
			if (game.AppId == 0)
			{
				var names = GetSearchNames(library.GamePath(game), game);
				var matches = new List<SteamMatch>();
				foreach (var name in names)
				{
					matches.AddRange(await Search(name, cancel));
					var exact = FindExactMatch(matches, name);
					if (exact != null)
					{
						discoveredId = exact.AppId;
						break;
					}
				}

				if (discoveredId == 0)
					return CompleteAttempt(new DiscoveryResult(
						matches.Count == 0 ? "Game not found on Steam; set its App ID in Settings." : "Choose the matching Steam game.",
						matches.DistinctBy(x => x.AppId).Take(30).ToList()
					));
			}

			metadata = await Fetch(discoveredId, cancel);
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or TimeoutException)
		{
			cancel.ThrowIfCancellationRequested();
			return CompleteAttempt(new DiscoveryResult("Steam discovery unavailable; existing IDs and DLC entries kept. Use Settings to retry the lookup."));
		}

		cancel.ThrowIfCancellationRequested();
		game.AppId = discoveredId;
		game.IsNonSteam = false;
		Merge(game, metadata, cache);
		SelectExecutable(library.GamePath(game), game, metadata);
		Storage.Write(library.SettingsFile(game, "Steam.json"), metadata);
		game.SteamDataAppId = game.AppId;
		game.SteamLookupAppId = game.AppId;
		library.SaveGame(game);
		return new DiscoveryResult("");
	}

	public static void Merge(Game game, SteamMetadata metadata, SteamMetadata previous)
	{
		if (metadata.AppId != game.AppId)
			throw new IOException("Steam metadata belongs to a different game.");
		var dlcs = Emulator.ParseDlc(game.DlcList);
		if (previous.AppId != 0 && previous.AppId != game.AppId)
			foreach (var (id, title) in previous.Dlcs)
				if (dlcs.GetValueOrDefault(id) == title)
					dlcs.Remove(id);
		foreach (var (id, title) in metadata.Dlcs)
			if (id != 0 && (!dlcs.ContainsKey(id) || dlcs[id] == previous.Dlcs.GetValueOrDefault(id)))
				dlcs[id] = CleanName(title) is { Length: > 0 } cleaned ? cleaned : $"DLC {id}";
		game.DlcList = string.Join("\n", dlcs.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));
		if (!string.IsNullOrWhiteSpace(metadata.Name))
			game.Name = metadata.Name;
	}

	public static void SelectExecutable(string root, Game game, SteamMetadata metadata)
	{
		var available = game.DetectedExecutables.Where(file => File.Exists(Storage.Inside(root, file))).ToList();
		var matches = metadata.Executables.Select(x => x.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)).Where(x => available.Contains(x, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		Library.SelectExecutables(root, game, matches);
	}

	public async Task<SteamMetadata> Fetch(uint appId, CancellationToken cancel = default)
	{
		if (appId == 0)
			throw new IOException("A Steam App ID is required.");
		try
		{
			return await FetchPicsMetadata(appId, cancel);
		}
		catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or HttpRequestException or SteamKit2.AsyncJobFailedException)
		{
			cancel.ThrowIfCancellationRequested();
			return await FetchStoreMetadata(appId, cancel);
		}
	}

	private static async Task<SteamMetadata> FetchPicsMetadata(uint appId, CancellationToken cancel)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		timeout.CancelAfter(TimeSpan.FromSeconds(25));
		var token = timeout.Token;
		var client = new SteamClient(SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.WebSocket | ProtocolTypes.Tcp)));
		var manager = new CallbackManager(client);
		var user = client.GetHandler<SteamUser>()!;
		var apps = client.GetHandler<SteamApps>()!;
		var loggedIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var connected = manager.Subscribe<SteamClient.ConnectedCallback>(_ => user.LogOnAnonymous());
		using var loggedOn = manager.Subscribe<SteamUser.LoggedOnCallback>(c =>
		{
			if (c.Result == EResult.OK)
				loggedIn.TrySetResult();
			else
				loggedIn.TrySetException(new IOException("Steam anonymous metadata is unavailable."));
		});
		using var disconnected = manager.Subscribe<SteamClient.DisconnectedCallback>(_ => loggedIn.TrySetException(new IOException("Steam disconnected.")));
		var pump = Task.Run(() =>
		{
			while (!token.IsCancellationRequested)
				manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
		});
		try
		{
			client.Connect();
			await loggedIn.Task.WaitAsync(token);

			async Task<Dictionary<uint, KeyValue>> ReadAppMetadata(IEnumerable<uint> ids)
			{
				var result = new Dictionary<uint, KeyValue>();
				foreach (var batch in ids.Distinct().Chunk(100))
				{
					var access = await apps.PICSGetAccessTokens(batch, []).ToTask().WaitAsync(token);
					var requests = batch.Select(id => new SteamApps.PICSRequest(id, access.AppTokens.GetValueOrDefault(id)));
					var response = await apps.PICSGetProductInfo(requests, []).ToTask().WaitAsync(token);
					if (!response.Complete || response.Results == null)
						throw new IOException("Steam metadata response was incomplete.");
					foreach (var page in response.Results)
						foreach (var (id, info) in page.Apps)
							result[id] = info.KeyValues;
				}

				return result;
			}

			var games = await ReadAppMetadata([appId]);
			if (!games.TryGetValue(appId, out var info) || info["common"]["name"].Value is not { Length: > 0 } name)
				throw new IOException("Steam did not return this game's metadata.");
			if (info["common"]["type"].Value?.Equals("dlc", StringComparison.OrdinalIgnoreCase) == true)
				throw new InvalidDataException("Choose the base game's App ID, not a DLC ID.");
			var ids = GetDlcIds(info);
			var titles = ids.Count == 0 ? [] : await ReadAppMetadata(ids);
			return new SteamMetadata
			{
				AppId = appId,
				Name = name,
				Source = "Steam",
				RetrievedAt = DateTimeOffset.UtcNow,
				Dlcs = ids.ToDictionary(id => id, id => CleanName(titles.GetValueOrDefault(id)?["common"]["name"].Value ?? $"DLC {id}")),
				Executables = info["config"]["launch"].Children.Where(x => string.IsNullOrEmpty(x["config"]["oslist"].Value) || x["config"]["oslist"].Value!.Contains("windows")).Select(x => x["executable"].Value ?? "").Where(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Distinct().ToList()
			};
		}
		finally
		{
			timeout.Cancel();
			client.Disconnect();
			await pump;
		}
	}

	private static HashSet<uint> GetDlcIds(KeyValue info)
	{
		var result = new HashSet<uint>();
		foreach (var text in (info["extended"]["listofdlc"].Value ?? "").Split(','))
			if (uint.TryParse(text.Trim(), out var id) && id != 0)
				result.Add(id);
		foreach (var depot in info["depots"].Children)
			if (uint.TryParse(depot["dlcappid"].Value, out var id) && id != 0)
				result.Add(id);
		return result;
	}

	private async Task<SteamMetadata> FetchStoreMetadata(uint appId, CancellationToken cancel)
	{
		using var json = await GetJson($"https://store.steampowered.com/api/appdetails/?appids={appId}&l=english", cancel);
		if (!json.RootElement.TryGetProperty(appId.ToString(), out var app) || !app.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True || !app.TryGetProperty("data", out var data))
			throw new IOException("Steam Store has no metadata for this App ID.");
		if (ReadJsonText(data, "type") == "dlc")
			throw new IOException("Choose the base game's App ID, not a DLC ID.");
		var result = new SteamMetadata
		{
			AppId = appId,
			Name = ReadJsonText(data, "name"),
			Source = "Steam Store",
			RetrievedAt = DateTimeOffset.UtcNow
		};
		if (data.TryGetProperty("dlc", out var dlcs) && dlcs.ValueKind == JsonValueKind.Array)
			foreach (var entry in dlcs.EnumerateArray())
				if (entry.ValueKind == JsonValueKind.Number && entry.TryGetUInt32(out var id) && id != 0)
					result.Dlcs[id] = $"DLC {id}";
		using var titles = await GetJson($"https://store.steampowered.com/api/dlcforapp/?appid={appId}&l=english", cancel);
		if (titles.RootElement.ValueKind == JsonValueKind.Object && titles.RootElement.TryGetProperty("dlc", out var entries) && entries.ValueKind == JsonValueKind.Array)
			foreach (var entry in entries.EnumerateArray())
				if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var id) &&
					id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out var value) && value > 0)
					result.Dlcs[value] = CleanName(ReadJsonText(entry, "name", $"DLC {value}"));
		return result;
	}

	private async Task<JsonDocument> GetJson(string url, CancellationToken cancel)
	{
		using var response = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancel);
		response.EnsureSuccessStatusCode();
		return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
	}

	private static string ReadJsonText(JsonElement element, string key, string fallback = "") => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
}
