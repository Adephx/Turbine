using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Turbine;

public sealed class Library
{
	public string Root
	{
		get;
	}

	public string GamesRoot => Storage.Inside(Root, "Games");

	public string SettingsRoot => Storage.Inside(Root, "Settings");

	public Preferences Preferences
	{
		get; private set;
	}

	public Emulator Emulator
	{
		get;
	}

	private SteamDiscovery? discovery;

	public SteamDiscovery Discovery => discovery ??= new();

	public Library(string root)
	{
		Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		Directory.CreateDirectory(GamesRoot);
		Directory.CreateDirectory(SettingsRoot);
		var preferencesPath = Storage.Inside(SettingsRoot, "Turbine.json");
		var legacyPath = Storage.Inside(SettingsRoot, "LANBOY.json");
		var hasPreferences = File.Exists(preferencesPath);
		Preferences = Storage.Read<Preferences>(hasPreferences ? preferencesPath : legacyPath);
		if (!hasPreferences)
			Preferences.BroadcastAddress = "";
		Preferences.Validate();
		if (!hasPreferences || Preferences.LegacyLanEnabled.HasValue || Preferences.LegacyOverlay.HasValue ||
			Preferences.LegacyNotifications.HasValue || Preferences.LegacyUnlockDlc.HasValue)
		{
			Emulator.MigratePreferences(SettingsRoot, Preferences);
			SavePreferences(Preferences with { LegacyLanEnabled = null, LegacyOverlay = null, LegacyNotifications = null, LegacyUnlockDlc = null });
		}
		MigrateNetworkIdentity();
		Emulator = new Emulator(this);
	}

	private static string GetFolderName(Game game)
	{
		if (string.IsNullOrWhiteSpace(game.Folder) || game.Folder.StartsWith('.') || game.Folder.IndexOfAny(['\\', '/']) >= 0)
			throw new IOException("Choose a game directly inside the Games folder.");
		return game.Folder;
	}

	public string GamePath(Game game) => Storage.Inside(GamesRoot, GetFolderName(game));

	public string SettingsPath(Game game) => Storage.Inside(SettingsRoot, GetFolderName(game));

	public string GameFile(Game game, string relative) => Storage.Inside(GamePath(game), relative);

	public string SettingsFile(Game game, string relative) => Storage.Inside(SettingsPath(game), relative);

	public void SavePreferences(Preferences preferences)
	{
		if (!string.IsNullOrEmpty(Preferences.ZeroTierIdentity))
			preferences = preferences with { ZeroTierIdentity = Preferences.ZeroTierIdentity };
		preferences.Validate();
		var path = Storage.Inside(SettingsRoot, "Turbine.json");
		FileSecurity? access = null;
		if (!string.IsNullOrEmpty(preferences.ZeroTierIdentity))
		{
			var legacy = Storage.Inside(SettingsRoot, "Turbine/Network/identity.secret");
			var source = File.Exists(legacy) ? legacy : path;
			if (File.Exists(source))
				access = new FileInfo(source).GetAccessControl(AccessControlSections.Access);
			else
			{
				access = new FileSecurity();
				access.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
			}
			access.SetAccessRuleProtection(true, true);
			access.PurgeAccessRules(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
		}
		Storage.Write(path, preferences, access);
		Preferences = preferences;
	}

	internal async Task<string> GetNetworkIdentityAsync(CancellationToken cancel)
	{
		if (string.IsNullOrEmpty(Preferences.ZeroTierIdentity))
		{
			var identity = await Task.Run(Networking.ZeroTierTransport.CreateIdentity, cancel);
			cancel.ThrowIfCancellationRequested();
			SavePreferences(Preferences with { ZeroTierIdentity = identity });
		}
		return Preferences.ZeroTierIdentity;
	}

	private void MigrateNetworkIdentity()
	{
		const string relative = "Turbine/Network";
		var directory = Storage.Inside(SettingsRoot, relative);
		if (!Directory.Exists(directory))
			return;
		var secretPath = Storage.Inside(directory, "identity.secret");
		var publicPath = Storage.Inside(directory, "identity.public");
		var identity = Preferences.ZeroTierIdentity;
		if (File.Exists(secretPath))
		{
			var legacy = File.ReadAllText(secretPath).Trim();
			Networking.ZeroTierTransport.ValidateIdentity(legacy);
			if (!string.IsNullOrEmpty(identity) && !string.Equals(identity, legacy, StringComparison.Ordinal))
				throw new IOException("The saved ZeroTier identities differ. Both copies have been kept.");
			identity = legacy;
		}
		if (string.IsNullOrEmpty(identity))
		{
			if (File.Exists(publicPath))
				throw new IOException("ZeroTier identity.secret is missing. Restore it to keep this node's authorization.");
			DeleteLegacyNetworkFiles(directory);
			return;
		}
		Networking.ZeroTierTransport.ValidateIdentity(identity);
		if (File.Exists(publicPath) && File.ReadAllText(publicPath).Trim() != identity[..identity.LastIndexOf(':')])
			throw new IOException("The saved ZeroTier identities differ. Both copies have been kept.");
		SavePreferences(Preferences with { ZeroTierIdentity = identity });
		var saved = Storage.Read<Preferences>(Storage.Inside(SettingsRoot, "Turbine.json"));
		if (saved.ZeroTierIdentity != identity)
			throw new IOException("Could not save the ZeroTier identity. The original files have been kept.");
		DeleteLegacyNetworkFiles(directory);
	}

	private static void DeleteLegacyNetworkFiles(string directory)
	{
		var files = new List<string> { Storage.Inside(directory, "identity.secret"), Storage.Inside(directory, "identity.public"), Storage.Inside(directory, "roots") };
		var peers = Storage.Inside(directory, "peers.d");
		if (Directory.Exists(peers))
			files.AddRange(Directory.EnumerateFiles(peers, "*.peer").Select(path => Storage.Inside(peers, Path.GetFileName(path))));
		foreach (var file in files)
			File.Delete(file);
		if (Directory.Exists(peers) && !Directory.EnumerateFileSystemEntries(peers).Any())
			Directory.Delete(peers);
		if (Directory.EnumerateFileSystemEntries(directory).Any())
			return;
		Directory.Delete(directory);
		var parent = Path.GetDirectoryName(directory)!;
		if (!Directory.EnumerateFileSystemEntries(parent).Any())
			Directory.Delete(parent);
	}

	public void SaveGame(Game game) => Storage.Write(SettingsFile(game, "Game.json"), game);

	public List<Game> Load(CancellationToken cancel = default)
	{
		cancel.ThrowIfCancellationRequested();
		var result = new List<Game>();
		foreach (var directory in Directory.EnumerateDirectories(SettingsRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
		{
			cancel.ThrowIfCancellationRequested();
			var folder = Path.GetFileName(directory);
			if (folder.StartsWith('.'))
				continue;
			var config = Storage.Inside(SettingsRoot, folder + "/Game.json");
			if (!File.Exists(config) || !Directory.Exists(Storage.Inside(GamesRoot, folder)))
				continue;
			var game = Storage.Read<Game>(config);
			game.Folder = folder;
			if (string.IsNullOrWhiteSpace(game.Name))
				game.Name = folder;
			result.Add(game);
		}

		return result;
	}

	internal void UpdateStatus(Game game, CancellationToken cancel = default, bool verify = false)
	{
		cancel.ThrowIfCancellationRequested();
		var migrate = game.SteamApiPaths == null;
		if (migrate)
		{
			var root = GamePath(game);
			var files = Storage.Files(root, cancel, searchPattern: "steam*");
			files.AddRange(Directory.EnumerateFiles(root, "*.vdf").Select(file => Storage.Inside(root, Path.GetFileName(file))));
			if (game.AppId == 0)
				game.AppId = DetectAppId(root, files, cancel);
			cancel.ThrowIfCancellationRequested();
			IdentifySteam(game, root, files);
			Emulator.Migrate(game);
		}

		var previous = game.SteamDllChecks;
		Emulator.CheckPatchStatus(game, verify);
		if (!migrate && ReferenceEquals(previous, game.SteamDllChecks))
			return;
		try
		{
			SaveGame(game);
		}
		catch (Exception ex) when (!migrate && (ex is IOException or UnauthorizedAccessException))
		{
			// The checked status still applies if the cache cannot be saved.
		}
	}

	private static void IdentifySteam(Game game, string root, List<string> files)
	{
		var knownSteam = !game.IsNonSteam && game.SteamApiPaths != null;
		game.SteamApiPaths = files.Where(Emulator.IsSteamDll).Select(file => Path.GetRelativePath(root, file)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		game.IsNonSteam = !knownSteam && game.AppId == 0 && !files.Any(file => Path.GetFileName(file).ToLowerInvariant() is "steam_api.dll" or "steam_api64.dll" or "steamclient.dll" or "steamclient64.dll" or "steam_appid.txt" or "steam_api.dll_backup" or "steam_api64.dll_backup" or "steam_api.dll.bak" or "steam_api64.dll.bak" or "steam_interfaces.txt" or "steamworks.net.txt");
	}

	public async Task Scan(IProgress<string>? progress = null, CancellationToken cancel = default, Func<Game, Task>? found = null)
	{
		cancel.ThrowIfCancellationRequested();
		var directories = new List<string>();
		foreach (var directory in Directory.EnumerateDirectories(GamesRoot))
		{
			cancel.ThrowIfCancellationRequested();
			directories.Add(directory);
		}

		directories.Sort(StringComparer.OrdinalIgnoreCase);
		cancel.ThrowIfCancellationRequested();
		foreach (var directory in directories)
		{
			cancel.ThrowIfCancellationRequested();
			var folder = Path.GetFileName(directory);
			if (folder.StartsWith('.'))
				continue;
			progress?.Report(folder);
			if (await Task.Run(() => ScanGame(directory, cancel), cancel) is { } game)
			{
				if (found != null)
					await found(game);
			}
		}
	}

	private Game? ScanGame(string directory, CancellationToken cancel = default)
	{
		cancel.ThrowIfCancellationRequested();
		var folder = Path.GetFileName(directory);
		if (folder.StartsWith('.'))
			return null;
		Storage.CheckLinks(directory);
		var config = Storage.Inside(SettingsRoot, folder + "/Game.json");
		var files = Storage.Files(directory, out var folderBytes, cancel);
		cancel.ThrowIfCancellationRequested();
		var candidates = FindExecutables(directory, files, cancel);
		var hasConfig = File.Exists(config);
		if (!hasConfig && candidates.Count == 0)
			return null;
		cancel.ThrowIfCancellationRequested();
		var game = hasConfig ? Storage.Read<Game>(config) : DetectGame(directory, folder, files, folderBytes, candidates, cancel);
		game.Folder = folder;
		if (game.LegacyExecutable is { Length: > 0 } legacy)
		{
			var executable = GameFile(game, legacy);
			if (File.Exists(executable))
			{
				var architecture = ExecutableArchitecture(executable);
				if (architecture == "x64")
				{
					game.Executable64 = legacy;
					game.Prefer64Bit = true;
				}

				if (architecture == "x86")
				{
					game.Executable32 = legacy;
					game.Prefer64Bit = false;
				}
			}

			game.LegacyExecutable = null;
		}

		if (string.IsNullOrWhiteSpace(game.Name))
			game.Name = folder;
		if (hasConfig)
		{
			if (game.AppId == 0)
				game.AppId = DetectAppId(directory, files, cancel);
			if (!game.DetectedExecutables.SequenceEqual(candidates, StringComparer.OrdinalIgnoreCase))
				SelectExecutables(directory, game, candidates, cancel);
			game.DetectedExecutables = candidates;
			game.FolderBytes = folderBytes;
			IdentifySteam(game, directory, files);
		}

		cancel.ThrowIfCancellationRequested();
		Emulator.Migrate(game);
		Emulator.CheckPatchStatus(game, verify: true);
		SaveGame(game);
		return game;
	}

	private static Game DetectGame(string root, string name, CancellationToken cancel)
	{
		cancel.ThrowIfCancellationRequested();
		var files = Storage.Files(root, out var folderBytes, cancel);
		return DetectGame(root, name, files, folderBytes, FindExecutables(root, files, cancel), cancel);
	}

	private static uint DetectAppId(string root, List<string> files, CancellationToken cancel)
	{
		var ids = new HashSet<uint>();
		foreach (var path in files)
		{
			cancel.ThrowIfCancellationRequested();
			if (!Path.GetFileName(path).Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase))
				continue;
			if (uint.TryParse(File.ReadAllText(path).Trim(), out var id) && id != 0)
				ids.Add(id);
		}

		if (ids.Count > 0)
			return ids.Count == 1 ? ids.Single() : 0;
		foreach (var path in files)
		{
			cancel.ThrowIfCancellationRequested();
			if (!Path.GetDirectoryName(path)!.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) || !Path.GetExtension(path).Equals(".vdf", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > 1_048_576)
				continue;
			var script = File.ReadAllText(path);
			if (!Regex.IsMatch(script, "\\A\\s*\"InstallScript\"\\s*\\{", RegexOptions.IgnoreCase))
				continue;
			foreach (Match match in Regex.Matches(script, @"""HasRunKey""\s+""[^""\r\n]*\\+Valve\\+Steam\\+Apps\\+(?<id>\d+)(?:\\+|"")", RegexOptions.IgnoreCase))
				if (uint.TryParse(match.Groups["id"].Value, out var id) && id != 0)
					ids.Add(id);
		}

		return ids.Count == 1 ? ids.Single() : 0;
	}

	private static Game DetectGame(string root, string name, List<string> files, long folderBytes, List<string> candidates, CancellationToken cancel)
	{
		var game = new Game
		{
			Name = name,
			FolderBytes = folderBytes,
			AppId = DetectAppId(root, files, cancel)
		};
		string? oldDlc = null;
		string? appConfig = null;
		foreach (var path in files)
		{
			cancel.ThrowIfCancellationRequested();
			if (oldDlc == null && Path.GetFileName(path).Equals("dlc.txt", StringComparison.OrdinalIgnoreCase))
				oldDlc = path;
			if (appConfig == null && Path.GetFileName(path).Equals("configs.app.ini", StringComparison.OrdinalIgnoreCase))
				appConfig = path;
			if (oldDlc != null && appConfig != null)
				break;
		}

		var dlcLines = new List<string>();
		if (appConfig != null)
		{
			var inDlc = false;
			foreach (var line in File.ReadLines(appConfig))
			{
				cancel.ThrowIfCancellationRequested();
				if (line.TrimStart().StartsWith('['))
					inDlc = line.Trim().Equals("[app::dlcs]", StringComparison.OrdinalIgnoreCase);
				if (inDlc && Regex.IsMatch(line, @"^\s*\d+\s*=.+"))
					dlcLines.Add(line.Trim());
			}
		}
		else if (oldDlc != null)
		{
			foreach (var line in File.ReadLines(oldDlc))
			{
				cancel.ThrowIfCancellationRequested();
				if (Regex.IsMatch(line, @"^\s*\d+\s*=.+"))
					dlcLines.Add(line.Trim());
			}
		}

		game.DlcList = string.Join("\n", dlcLines);
		SelectExecutables(root, game, candidates, cancel);
		game.DetectedExecutables = candidates;
		IdentifySteam(game, root, files);
		return game;
	}

	private static List<string> FindExecutables(string root, List<string> files, CancellationToken cancel)
	{
		var candidates = new List<string>();
		foreach (var file in files)
		{
			cancel.ThrowIfCancellationRequested();
			if (!Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase))
				continue;
			var relative = Path.GetRelativePath(root, file);
			if (!Regex.IsMatch(relative, @"(?i)(unins|vcredist|dxsetup|crashreport|crashpad|redist|dotnet|unitycrash|setup\.exe|easyanticheat|battleye)"))
				candidates.Add(relative);
		}

		candidates.Sort((left, right) =>
		{
			var depth = left.Count(c => c == Path.DirectorySeparatorChar).CompareTo(right.Count(c => c == Path.DirectorySeparatorChar));
			return depth != 0 ? depth : Comparer<string>.Default.Compare(left, right);
		});
		cancel.ThrowIfCancellationRequested();
		return candidates;
	}

	public static string? ExecutableArchitecture(string file)
	{
		try
		{
			return Emulator.Architecture(file);
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	public static void SelectExecutables(string root, Game game, IEnumerable<string> candidates, CancellationToken cancel = default)
	{
		var hadSelection = game.ExecutablePaths.Any();
		var normalizedName = SteamDiscovery.Normalize(game.Name);
		var choices = new List<(string Path, string? Architecture)>();
		foreach (var path in candidates)
		{
			cancel.ThrowIfCancellationRequested();
			choices.Add((path, ExecutableArchitecture(Storage.Inside(root, path))));
		}

		string Select(string architecture)
		{
			var matching = new List<string>();
			foreach (var choice in choices)
			{
				cancel.ThrowIfCancellationRequested();
				if (choice.Architecture == architecture)
					matching.Add(choice.Path);
			}

			if (matching.Count == 1)
				return matching[0];
			var names = new List<string>();
			foreach (var path in matching)
			{
				cancel.ThrowIfCancellationRequested();
				if (SteamDiscovery.Normalize(Regex.Replace(Path.GetFileNameWithoutExtension(path), @"(?i)([-_ ]?(win64|win32|x64|x86|shipping))+$", "")) == normalizedName)
					names.Add(path);
			}

			return names.Count == 1 ? names[0] : "";
		}

		if (string.IsNullOrWhiteSpace(game.Executable32))
			game.Executable32 = Select("x86");
		if (string.IsNullOrWhiteSpace(game.Executable64))
			game.Executable64 = Select("x64");
		if (!hadSelection)
			game.Prefer64Bit = !string.IsNullOrWhiteSpace(game.Executable64);
	}

	public async Task<Game> Import(string source, CancellationToken cancel)
	{
		source = Path.GetFullPath(source).TrimEnd('\\');
		var folder = Path.GetFileName(source);
		if (string.IsNullOrWhiteSpace(folder) || folder.StartsWith('.'))
			throw new IOException("Choose a named game folder.");
		var target = Storage.Inside(GamesRoot, folder);
		if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
			return ScanGame(target, cancel) ?? throw new IOException("No game executable found in this folder.");
		if (source.Equals(Root, StringComparison.OrdinalIgnoreCase) || source.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase) || Root.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase))
			throw new IOException("Choose a game folder outside Turbine, or put it directly in Games and use Refresh.");
		if (Directory.Exists(target))
			throw new IOException("Games already contains a folder with that name. Nothing was overwritten.");
		var stagingName = ".import-" + Guid.NewGuid().ToString("N");
		var staging = Storage.Inside(GamesRoot, stagingName);
		Directory.CreateDirectory(staging);
		Game game;
		try
		{
			await Storage.CopyTree(source, staging, cancel);
			game = await Task.Run(() => DetectGame(staging, folder, cancel), cancel);
			if (game.DetectedExecutables.Count == 0)
				throw new IOException("No game executable found in this folder.");
			cancel.ThrowIfCancellationRequested();
			Directory.Move(staging, target);
		}
		finally
		{
			if (Directory.Exists(staging))
				Storage.DeleteTree(GamesRoot, stagingName);
		}

		game.Folder = folder;
		Emulator.Migrate(game);
		Emulator.CheckPatchStatus(game, verify: true);
		SaveGame(game);
		return game;
	}

	public void EnsureNotRunning(Game game)
	{
		var root = GamePath(game) + Path.DirectorySeparatorChar;
		foreach (var process in Process.GetProcesses())
		{
			using (process)
			{
				string? executable;
				try
				{
					executable = process.MainModule?.FileName;
				}
				catch (System.ComponentModel.Win32Exception)
				{
					continue;
				}
				catch (InvalidOperationException)
				{
					continue;
				}

				if (executable?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true)
					throw new IOException("Close this game before changing or removing its files.");
			}
		}
	}

	public ProcessStartInfo LaunchInfo(Game game)
	{
		if (string.IsNullOrWhiteSpace(game.Executable))
			throw new IOException("Choose the default game executable in Settings first.");
		var executable = GameFile(game, game.Executable);
		if (!File.Exists(executable))
			throw new IOException("The selected executable no longer exists. Choose it in Settings.");
		if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Emulator.Architecture(executable) != (game.Prefer64Bit ? "x64" : "x86"))
			throw new IOException("Choose an executable matching the default architecture in Settings.");
		var info = new ProcessStartInfo(executable)
		{
			WorkingDirectory = Path.GetDirectoryName(executable)!,
			Arguments = game.Arguments,
			UseShellExecute = false
		};
		if (game.IsNonSteam || game.AppId == 0)
			return info;
		UpdateStatus(game);
		if (game.IsPatched)
		{
			info.Environment["SteamAppId"] = game.AppId.ToString();
			info.Environment["SteamGameId"] = game.AppId.ToString();
			info.Environment["GseSavePath"] = SettingsFile(game, "Saves");
		}

		return info;
	}

	public void DeleteUserSettings(Game game)
	{
		if (game.AppId == 0)
			throw new IOException("Set a Steam App ID before deleting user settings.");
		EnsureNotRunning(game);
		Storage.DeleteTree(SettingsFile(game, "Saves"), game.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	public void DeleteAllSettings(Game game)
	{
		EnsureNotRunning(game);
		Storage.DeleteTree(SettingsRoot, GetFolderName(game));
	}

	public void DeleteGame(Game game, bool deleteSettings)
	{
		EnsureNotRunning(game);
		Storage.Files(GamePath(game));
		if (deleteSettings)
			Storage.Files(SettingsPath(game));
		Storage.DeleteTree(GamesRoot, GetFolderName(game));
		if (deleteSettings)
			Storage.DeleteTree(SettingsRoot, GetFolderName(game));
	}
}
