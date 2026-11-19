using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Turbine;

public sealed class Emulator(Library library)
{
	private string RuntimeRoot => Storage.Inside(library.Root, "Dependencies/Goldberg");

	private bool Available => new[]
	{
		"x86/steam_api.dll",
		"x64/steam_api64.dll"
	}.All(relative => File.Exists(Storage.Inside(RuntimeRoot, relative)));

	internal static bool IsSteamDll(string path) => string.Equals(Path.GetFileName(path), "steam_api.dll", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(path), "steam_api64.dll", StringComparison.OrdinalIgnoreCase);

	public bool CheckPatchStatus(Game game, bool verify = false)
	{
		if (game.IsNonSteam)
		{
			game.SteamDllChecks = null;
			return false;
		}
		var root = library.GamePath(game);
		var paths = game.SteamApiPaths ??= Storage.Files(root, searchPattern: "steam_api*.dll").Where(IsSteamDll).Select(file => Path.GetRelativePath(root, file)).ToList();
		var previous = game.SteamDllChecks;
		var checks = new Dictionary<string, SteamDllCheck>(StringComparer.OrdinalIgnoreCase);
		foreach (var relative in paths.Where(IsSteamDll))
		{
			var api = Storage.Inside(root, relative);
			try
			{
				var file = new FileInfo(api);
				if (!file.Exists)
					continue;
				var cached = previous?.GetValueOrDefault(relative);
				if (!verify && cached != null && cached.Length == file.Length && cached.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks)
					checks[relative] = cached;
				else
				{
					using var reader = new BinaryReader(file.OpenRead());
					var (architecture, _) = ReadArchitecture(reader, api);
					var stream = reader.BaseStream;
					stream.Position = 0;
					var patched = architecture != null && stream.Length <= Array.MaxLength && IsGoldberg(reader.ReadBytes((int)stream.Length));
					checks[relative] = new SteamDllCheck(stream.Length, File.GetLastWriteTimeUtc(api).Ticks, patched);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Leave unreadable DLLs unchecked so the next attempt retries them.
			}
		}
		if (previous == null || checks.Count != previous.Count || checks.Any(pair => previous.GetValueOrDefault(pair.Key) != pair.Value))
			game.SteamDllChecks = checks;
		return game.IsPatched;
	}

	private static bool IsGoldberg(ReadOnlySpan<byte> bytes) => (bytes.IndexOf("GSE Saves"u8) >= 0 || bytes.IndexOf("Goldberg SteamEmu Saves"u8) >= 0) && bytes.IndexOf("steam_interfaces.txt"u8) >= 0 && bytes.IndexOf("SteamAPI_Init"u8) >= 0;

	public void Migrate(Game game)
	{
		if (game.IsNonSteam)
			return;
		var oldRoot = library.SettingsFile(game, "Originals");
		if (Directory.Exists(oldRoot))
		{
			foreach (var source in Storage.Files(oldRoot).Where(IsSteamDll))
			{
				var relative = Path.GetRelativePath(oldRoot, source);
				var target = library.GameFile(game, relative + "_backup");
				var bytes = File.ReadAllBytes(source);
				var expected = Path.GetFileName(source).Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase) ? "x64" : "x86";
				if (Architecture(source) != expected || ReadOriginalInterfaces(bytes) == null)
					throw new IOException("The saved Steam API DLL is not a valid original.\nRestore a clean copy of this game's DLL.");
				if (File.Exists(target))
				{
					var existing = File.ReadAllBytes(target);
					if (Architecture(target) != expected || ReadOriginalInterfaces(existing) == null)
						throw new IOException("The DLL backup beside this game is not a valid original.");
					if (!existing.AsSpan().SequenceEqual(bytes))
						continue;
				}

				Storage.WriteBytes(target, bytes);
				if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
					throw new IOException("Could not verify the original DLL backup.");
				if (File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes))
					File.Delete(source);
			}

			foreach (var directory in Directory.EnumerateDirectories(oldRoot, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length).Append(oldRoot))
			{
				Storage.CheckLinks(directory);
				if (!Directory.EnumerateFileSystemEntries(directory).Any())
					Directory.Delete(directory);
			}
		}

		var oldState = library.SettingsFile(game, "Patch.json");
		if (!File.Exists(oldState))
			return;
		try
		{
			using var state = JsonDocument.Parse(File.ReadAllText(oldState));
			if (game.ManagedBroadcastAddress == null && state.RootElement.ValueKind == JsonValueKind.Object && state.RootElement.TryGetProperty("ManagedBroadcastAddress", out var entry) && entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } address && BroadcastNetworks.IsValidAddress(address))
				game.ManagedBroadcastAddress = address;
		}
		catch (JsonException)
		{
		}

		library.SaveGame(game);
		File.Delete(oldState);
	}

	public static string Architecture(string file)
	{
		using var reader = new BinaryReader(File.OpenRead(file));
		var (name, error) = ReadArchitecture(reader, file);
		return name ?? throw new IOException(error);
	}

	private static (string? Name, string? Error) ReadArchitecture(BinaryReader reader, string file)
	{
		if (reader.BaseStream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
			return (null, $"Not a Windows executable: {file}");
		reader.BaseStream.Position = 0x3C;
		var offset = reader.ReadInt32();
		if (offset < 64 || offset > reader.BaseStream.Length - 24)
			return (null, "Invalid PE header.");
		reader.BaseStream.Position = offset;
		if (reader.ReadUInt32() != 0x00004550)
			return (null, "Invalid PE signature.");
		return reader.ReadUInt16() switch
		{
			0x14c => ("x86", null),
			0x8664 => ("x64", null),
			_ => (null, "Only x86 and x64 Steam games are supported.")
		};
	}

	private string GetRuntimePath(string arch) => Storage.Inside(RuntimeRoot, arch + "/" + (arch == "x64" ? "steam_api64.dll" : "steam_api.dll"));

	private const string InterfacePattern = @"(?:(?<family>SteamClient|SteamGameServerStats|SteamGameServer|SteamMatchMakingServers|SteamMatchMaking|SteamUser|SteamFriends|SteamUtils|STEAMUSERSTATS_INTERFACE_VERSION|STEAMAPPS_INTERFACE_VERSION|SteamApps|SteamNetworking|STEAMREMOTESTORAGE_INTERFACE_VERSION|STEAMSCREENSHOTS_INTERFACE_VERSION|STEAMHTTP_INTERFACE_VERSION|STEAMUNIFIEDMESSAGES_INTERFACE_VERSION|STEAMCONTROLLER_INTERFACE_VERSION_?|SteamController|STEAMUGC_INTERFACE_VERSION|STEAMAPPLIST_INTERFACE_VERSION|STEAMMUSIC_INTERFACE_VERSION|STEAMMUSICREMOTE_INTERFACE_VERSION|STEAMHTMLSURFACE_INTERFACE_VERSION_|STEAMINVENTORY_INTERFACE_V|STEAMVIDEO_INTERFACE_V|SteamMasterServerUpdater)\d{3}|(?<family>STEAM(?:CONTROLLER|UGC)_INTERFACE_VERSION))";

	private static string[] ReadInterfaceVersions(string text, bool configuration)
	{
		var pattern = configuration ? @"\A" + InterfacePattern + @"\z" : @"(?<![A-Za-z0-9_])" + InterfacePattern + @"(?=\x00)";
		var matches = configuration ? text.Replace("\r", "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';')).Select(line => Regex.Match(line, pattern)).ToArray() : Regex.Matches(text, pattern).ToArray();
		if (matches.Any(match => !match.Success))
			throw new IOException("Invalid steam_interfaces.txt.\nRegenerate it from the game's original Steam API DLL.");
		var versions = matches.DistinctBy(match => match.Value).ToArray();
		if (!configuration && versions.Any(match => match.Value == "SteamClient017"))
			versions = versions.Where(match => match.Groups["family"].Value != "SteamClient" || match.Value == "SteamClient017").ToArray();
		if (versions.GroupBy(match => match.Groups["family"].Value.TrimEnd('_') switch
		{
			"SteamApps" => "STEAMAPPS_INTERFACE_VERSION",
			"SteamController" => "STEAMCONTROLLER_INTERFACE_VERSION",
			var family => family
		}).Any(group => group.Count() > 1))
			throw new IOException("Steam interface versions are ambiguous.\nUse the game's original Steam API DLL.");
		return versions.Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();
	}

	private static string[]? ReadOriginalInterfaces(byte[] bytes)
	{
		if (IsGoldberg(bytes))
			return null;
		try
		{
			var versions = ReadInterfaceVersions(Encoding.Latin1.GetString(bytes), false);
			return versions.Length > 0 ? versions : null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	public static Dictionary<uint, string> ParseDlc(string? input)
	{
		var result = new Dictionary<uint, string>();
		if (string.IsNullOrWhiteSpace(input))
			return result;
		foreach (var line in input.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
		{
			var parts = line.Split('=', 2);
			if (parts.Length != 2 || !uint.TryParse(parts[0].Trim(), out var id) || id == 0 || string.IsNullOrWhiteSpace(parts[1]))
				throw new IOException("DLC entries must be one AppID=Name per line, for example 123456=Expansion.");
			result[id] = parts[1].Trim();
		}

		return result;
	}

	internal static void MigratePreferences(string settingsRoot, Preferences previous)
	{
		var networking = previous.LegacyLanEnabled == false;
		var overlay = previous.LegacyOverlay == false || previous.LegacyNotifications == false;
		var dlc = previous.LegacyUnlockDlc == false;
		if (!networking && !overlay && !dlc)
			return;

		foreach (var directory in Directory.EnumerateDirectories(settingsRoot))
		{
			var folder = Path.GetFileName(directory);
			if (folder.StartsWith('.'))
				continue;
			var config = Storage.Inside(settingsRoot, Path.Combine(folder, "Game.json"));
			if (!File.Exists(config))
				continue;
			void Update(string file, Action<Ini> apply)
			{
				var path = Storage.Inside(settingsRoot, Path.Combine(folder, "Saves/settings", file));
				if (!File.Exists(path))
					return;
				var ini = new Ini(File.ReadAllText(path));
				apply(ini);
				Storage.WriteText(path, ini.ToString());
			}

			if (networking)
				Update("configs.main.ini", EnableNetworking);
			if (overlay)
				Update("configs.overlay.ini", EnableOverlay);
			if (dlc)
				Update("configs.app.ini", ini => EnableDlc(ini, Storage.Read<Game>(config).DlcList));
		}
	}

	private static void EnableNetworking(Ini ini)
	{
		ini.Set("main::connectivity", "disable_networking", "0");
		ini.Set("main::connectivity", "disable_lobby_creation", "0");
	}

	private static void EnableOverlay(Ini ini)
	{
		ini.Set("overlay::general", "enable_experimental_overlay", "1");
		ini.Set("overlay::general", "disable_achievement_notification", "0");
		ini.Set("overlay::general", "disable_friend_notification", "0");
	}

	private static void EnableDlc(Ini ini, string list)
	{
		ini.Set("app::dlcs", "unlock_all", "1");
		foreach (var (id, title) in ParseDlc(list))
			ini.Set("app::dlcs", id.ToString(), title);
	}

	private Dictionary<string, string> BuildConfigurations(Game game, IEnumerable<string>? localSettings = null)
	{
		var result = new Dictionary<string, string>();
		foreach (var name in new[]
		{
			"user",
			"main",
			"app",
			"overlay"
		})
		{
			var fileName = "configs." + name + ".ini";
			var sources = (localSettings ?? []).Select(directory => Storage.Inside(directory, fileName)).Append(library.SettingsFile(game, "Config/" + fileName)).Append(library.SettingsFile(game, "Saves/settings/" + fileName));
			var texts = sources.Where(File.Exists).Select(File.ReadAllText).ToArray();
			var primaryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var inherited = new Dictionary<string, (string Section, string Key, string Value)>(StringComparer.OrdinalIgnoreCase);
			var primaryLines = new List<string>();
			for (var source = 0; source < texts.Length; source++)
			{
				var section = "";
				foreach (var line in texts[source].Replace("\r", "").Split('\n'))
				{
					var text = line.Trim();
					if (text.StartsWith('[') && text.EndsWith(']'))
						section = text[1..^1];
					var parts = text.Split('=', 2);
					var setting = parts.Length == 2 && !text.StartsWith(';') && !text.StartsWith('#') && section.Length > 0;
					if (setting && section.Equals("user::saves", StringComparison.OrdinalIgnoreCase) && parts[0].Trim().Equals("local_save_path", StringComparison.OrdinalIgnoreCase))
						continue;
					if (source == texts.Length - 1)
						primaryLines.Add(line);
					if (!setting)
						continue;
					var key = section + "\n" + parts[0].Trim();
					inherited[key] = (section, parts[0].Trim(), parts[1].Trim());
					if (source == texts.Length - 1)
						primaryKeys.Add(key);
				}
			}

			var ini = new Ini(string.Join("\n", primaryLines));
			foreach (var (key, value) in inherited)
				if (!primaryKeys.Contains(key))
					ini.Set(value.Section, value.Key, value.Value);
			switch (name)
			{
				case "user":
					ini.Set("user::general", "account_name", library.Preferences.Username.Trim());
					ini.Set("user::general", "account_steamid", library.Preferences.SteamId);
					ini.Set("user::general", "language", library.Preferences.Language.ToLowerInvariant());
					break;
				case "main":
					EnableNetworking(ini);
					ini.Set("main::connectivity", "offline", "0");
					ini.Set("main::connectivity", "disable_lan_only", "0");
					ini.Set("main::connectivity", "listen_port", "47584");
					break;
				case "app":
					ini.RemoveNumericKeys("app::dlcs");
					EnableDlc(ini, game.DlcList);
					break;
				case "overlay":
					EnableOverlay(ini);
					ini.Set("overlay::general", "show_button_test_achievement", "0");
					ini.Set("overlay::general", "disable_warning_local_save", "1");
					break;
			}

			result[fileName] = ini.ToString();
		}

		return result;
	}

	public bool UpdateSettings(Game game)
	{
		library.Preferences.Validate();
		if (!UpdateSavePaths(game))
			return false;
		foreach (var (file, text) in BuildConfigurations(game))
			Storage.WriteText(library.SettingsFile(game, "Saves/settings/" + file), text);
		SaveBroadcastSettings(game);
		return true;
	}

	public bool UpdateBroadcastSettings(Game game)
	{
		library.Preferences.Validate();
		if (!UpdateSavePaths(game))
			return false;
		SaveBroadcastSettings(game);
		return true;
	}

	private bool UpdateSavePaths(Game game)
	{
		if (!CheckPatchStatus(game))
			return false;
		var files = game.SteamApiPaths!.Select(relative => library.GameFile(game, relative));
		var saves = library.SettingsFile(game, "Saves");
		var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var directory in files.Where(IsSteamDll).Select(api => Path.GetDirectoryName(api)!).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			var path = Storage.Inside(directory, "steam_settings/configs.user.ini");
			if (!File.Exists(path))
				return false;
			var text = File.ReadAllText(path);
			var redirect = ReadSavePath(text);
			if (string.IsNullOrWhiteSpace(redirect))
				return false;
			var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(redirect, directory));
			if (current.Equals(saves, StringComparison.OrdinalIgnoreCase))
				continue;
			var oldFolder = Path.GetDirectoryName(current);
			if (!Path.GetFileName(current).Equals("Saves", StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(Path.GetDirectoryName(oldFolder), library.SettingsRoot, StringComparison.OrdinalIgnoreCase) || Path.Exists(oldFolder))
				return false;
			var ini = new Ini(text);
			ini.Set("user::saves", "local_save_path", Path.GetRelativePath(directory, saves).Replace('\\', '/'));
			updates[path] = ini.ToString();
		}

		foreach (var (path, text) in updates)
			Storage.WriteText(path, text);
		return true;
	}

	private static string? ReadSavePath(string text)
	{
		var inside = false;
		var found = false;
		string? value = null;
		foreach (var line in text.Split('\n'))
		{
			var entry = line.Trim();
			if (entry.StartsWith('['))
			{
				inside = entry.Equals("[user::saves]", StringComparison.OrdinalIgnoreCase);
				if (inside && found)
					return null;
				found |= inside;
			}
			else if (inside)
			{
				var parts = entry.Split('=', 2);
				if (parts.Length != 2 || !parts[0].Trim().Equals("local_save_path", StringComparison.OrdinalIgnoreCase))
					continue;
				if (value != null)
					return null;
				value = parts[1].Trim();
			}
		}

		return value;
	}

	private void SaveBroadcastSettings(Game game)
	{
		var path = library.SettingsFile(game, "Saves/settings/custom_broadcasts.txt");
		var before = File.Exists(path) ? File.ReadAllText(path) : "";
		var text = before;
		var address = library.Preferences.BroadcastAddress;
		var previousAddress = game.ManagedBroadcastAddress;
		if (game.ManagedBroadcastAddress is { Length: > 0 } old && old != address)
		{
			var matches = Regex.Matches(text, @"(?m)^[ \t]*" + Regex.Escape(old) + @"[ \t]*\r?(?:\n|$)");
			if (matches.Count > 0)
			{
				var match = matches[^1];
				text = text.Remove(match.Index, match.Length);
			}

			game.ManagedBroadcastAddress = null;
		}

		if (address.Length > 0 && !text.Split('\n').Any(line => line.Trim() == address))
		{
			var newline = text.Contains("\r\n") ? "\r\n" : "\n";
			if (text.Length > 0 && !text.EndsWith('\n'))
				text += newline;
			text += address + newline;
			game.ManagedBroadcastAddress = address;
		}

		byte[]? original = null;
		var changed = text != before;
		if (changed && File.Exists(path))
			original = File.ReadAllBytes(path);
		try
		{
			if (changed)
			{
				if (text.Length == 0)
					File.Delete(path);
				else
					Storage.WriteText(path, text);
			}

			library.SaveGame(game);
		}
		catch
		{
			game.ManagedBroadcastAddress = previousAddress;
			if (changed)
			{
				if (original != null)
					Storage.WriteBytes(path, original);
				else if (File.Exists(path))
					File.Delete(path);
			}

			throw;
		}
	}

	public void Apply(Game game)
	{
		try
		{
			PatchFiles(game);
		}
		finally
		{
			library.UpdateStatus(game, verify: true);
		}
	}

	private void PatchFiles(Game game)
	{
		if (game.IsNonSteam)
			throw new IOException("This game does not need a Steam patch.");
		library.EnsureNotRunning(game);
		library.Preferences.Validate();
		if (game.AppId == 0)
			throw new IOException("Set this game's Steam App ID first.");
		if (!Available)
			throw new IOException("The emulator dependency is missing. Restart Turbine to retry its download.");
		Migrate(game);
		var root = library.GamePath(game);
		var apis = Storage.Files(root, searchPattern: "steam_api*.dll").Where(IsSteamDll).ToList();
		if (apis.Count == 0)
			throw new IOException("No Steam API DLL found. This game needs a different integration; its files were not changed.");
		var writes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		var settings = apis.Select(api => Storage.Inside(Path.GetDirectoryName(api)!, "steam_settings")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		var configs = BuildConfigurations(game, settings);
		foreach (var api in apis)
		{
			var arch = Architecture(api);
			var runtime = GetRuntimePath(arch);
			if (Architecture(runtime) != arch)
				throw new IOException("The emulator dependency has the wrong architecture. Restart Turbine to check and repair it.");
			var directory = Path.GetDirectoryName(api)!;
			var relative = Path.GetRelativePath(root, api);
			var replacement = File.ReadAllBytes(runtime);
			if (!IsGoldberg(replacement))
				throw new IOException("The Goldberg dependency is invalid.");
			var installed = File.ReadAllBytes(api);
			var patched = IsGoldberg(installed);
			var original = library.GameFile(game, relative + "_backup");
			var versions = patched ? null : ReadOriginalInterfaces(installed);
			if (File.Exists(original))
			{
				if (Architecture(original) != arch)
					throw new IOException("The saved original Steam API DLL has the wrong architecture.");
				var saved = File.ReadAllBytes(original);
				var savedVersions = ReadOriginalInterfaces(saved) ?? throw new IOException("The saved Steam API DLL is not a valid original.\nRestore a clean copy of this game's DLL.");
				if (patched || installed.AsSpan().SequenceEqual(saved))
					versions = savedVersions;
				else if (versions == null)
					throw new IOException("The installed Steam API DLL has changed and its versions are ambiguous.\nRestore a clean copy before patching.");
				else
					originals[original] = installed;
			}
			else if (!patched)
			{
				if (versions == null)
					throw new IOException("Could not verify the installed Steam API DLL as an original.\nRestore a clean copy before patching.");
				originals[original] = installed;
			}
			writes[relative] = replacement;
			var bootstrap = new Ini();
			bootstrap.Set("user::saves", "local_save_path", Path.GetRelativePath(directory, library.SettingsFile(game, "Saves")).Replace('\\', '/'));
			writes[Path.GetRelativePath(root, Path.Combine(directory, "steam_settings", "configs.user.ini"))] = Encoding.UTF8.GetBytes(bootstrap.ToString());
			writes[Path.GetRelativePath(root, Path.Combine(directory, "steam_settings", "steam_appid.txt"))] = Encoding.ASCII.GetBytes(game.AppId + "\n");
			var interfaces = Path.Combine(directory, "steam_settings", "steam_interfaces.txt");
			if (versions != null)
				writes[Path.GetRelativePath(root, interfaces)] = Encoding.ASCII.GetBytes(string.Join("\n", versions) + "\n");
			else
			{
				if (!File.Exists(interfaces) || ReadInterfaceVersions(File.ReadAllText(interfaces), true).Length == 0)
					throw new IOException("Could not generate steam_interfaces.txt.\nUse this game's original Steam API DLL.");
			}
		}

		foreach (var relative in writes.Keys)
			library.GameFile(game, relative);
		var executables = game.ExecutablePaths.Distinct(StringComparer.OrdinalIgnoreCase).Select(relative => (Relative: relative, Path: library.GameFile(game, relative))).ToArray();
		foreach (var executable in executables)
			if (!File.Exists(executable.Path))
				throw new IOException("The selected executable is missing.");
		foreach (var (relative, exe) in executables)
		{
			if (SteamStub.NeedsUnpacking(exe))
				writes[relative] = SteamStub.Unpack(library, game, exe);
		}

		foreach (var (file, bytes) in originals)
			Storage.WriteBytes(file, bytes);
		foreach (var (file, text) in configs)
			Storage.WriteText(library.SettingsFile(game, "Saves/settings/" + file), text);
		foreach (var (relative, bytes) in writes.OrderBy(write => IsSteamDll(write.Key)))
			Storage.WriteBytes(library.GameFile(game, relative), bytes);
		foreach (var file in configs.Keys)
		{
			var legacy = library.SettingsFile(game, "Config/" + file);
			if (File.Exists(legacy))
				File.Delete(legacy);
			if (file == "configs.user.ini")
				continue;
			foreach (var directory in settings)
			{
				var local = Storage.Inside(directory, file);
				if (File.Exists(local))
					File.Delete(local);
			}
		}

		var oldConfig = library.SettingsFile(game, "Config");
		if (Directory.Exists(oldConfig) && !Directory.EnumerateFileSystemEntries(oldConfig).Any())
			Directory.Delete(oldConfig);
		foreach (var name in new[]
		{
			"account_name.txt",
			"user_steam_id.txt",
			"language.txt",
			"listen_port.txt"
		})
		{
			var legacy = library.SettingsFile(game, "Saves/settings/" + name);
			if (File.Exists(legacy))
				File.Delete(legacy);
		}

		game.SteamApiPaths = apis.Select(api => Path.GetRelativePath(root, api)).ToList();
		SaveBroadcastSettings(game);
	}
}
