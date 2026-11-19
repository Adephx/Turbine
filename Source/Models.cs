using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Turbine;

public sealed record Preferences
{
	public string Username { get; set; } = "Player";

	public string SteamId { get; set; } = NewId();

	public string Language { get; set; } = "english";

	public string ZeroTierNetwork { get; set; } = "";

	public string ZeroTierIdentity { get; set; } = "";

	public bool UseZeroTier
	{
		get; set;
	}

	[JsonPropertyName("LanEnabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? LegacyLanEnabled { get; set; }

	[JsonPropertyName("Overlay"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? LegacyOverlay { get; set; }

	[JsonPropertyName("UnlockDlc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? LegacyUnlockDlc { get; set; }

	[JsonPropertyName("Notifications"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? LegacyNotifications { get; set; }

	public string Theme { get; set; } = Themes.DefaultId;

	public string BroadcastAddress { get; set; } = "";

	public static string NewId() => (76561197960265728UL + (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue)).ToString();

	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(Username) || Username.Length > 64 || Username.Any(char.IsControl) || Username.IndexOfAny([';', '#', '=']) >= 0)
			throw new InvalidOperationException("Use a player name of 1–64 characters without control characters, ;, # or =.");
		if (!ulong.TryParse(SteamId, out var id) || id <= 76561197960265728UL || id > 76561202255233023UL)
			throw new InvalidOperationException("Enter a valid individual Steam64 ID, or choose Generate.");
		if (string.IsNullOrWhiteSpace(Language) || Language.Any(c => !char.IsAsciiLetter(c) && c != '-'))
			throw new InvalidOperationException("Use a Steam language code, such as english, german or schinese.");
		if (!string.IsNullOrEmpty(ZeroTierNetwork) && !IsValidNetworkId(ZeroTierNetwork))
			throw new InvalidOperationException("Enter a valid 16-digit ZeroTier network ID.");
		if (UseZeroTier && !IsValidNetworkId(ZeroTierNetwork))
			throw new InvalidOperationException("Set the ZeroTier network ID in Settings first.");
		if (BroadcastAddress != "" && !BroadcastNetworks.IsValidAddress(BroadcastAddress))
			throw new InvalidOperationException("Invalid saved broadcast address.");
	}

	public static bool IsValidNetworkId(string? value) => value?.Length == 16 && ulong.TryParse(value, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var id) && id != 0;
}

public sealed record SteamDllCheck(long Length, long LastWriteUtcTicks, bool Patched);

public sealed class Game
{
	public string Name { get; set; } = "";

	public uint AppId
	{
		get; set;
	}

	public string Executable32 { get; set; } = "";

	public string Executable64 { get; set; } = "";

	public bool Prefer64Bit { get; set; } = true;

	[JsonIgnore]
	public string Executable => Prefer64Bit ? Executable64 : Executable32;

	[JsonIgnore]
	public IEnumerable<string> ExecutablePaths => new[]
	{
		Executable32,
		Executable64
	}.Where(x => !string.IsNullOrWhiteSpace(x));

	[JsonPropertyName("Executable"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? LegacyExecutable
	{
		get; set;
	}

	public string Arguments { get; set; } = "";

	public string DlcList { get; set; } = "";

	public List<string> DetectedExecutables { get; set; } = [];

	public uint SteamDataAppId
	{
		get; set;
	}

	public bool IsNonSteam
	{
		get; set;
	}

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public List<string>? SteamApiPaths
	{
		get; set;
	}

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public Dictionary<string, SteamDllCheck>? SteamDllChecks { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public uint? SteamLookupAppId
	{
		get; set;
	}

	[JsonIgnore]
	public bool NeedsSteamLookup => !IsNonSteam && SteamLookupAppId != AppId && (AppId == 0 || SteamDataAppId != AppId);

	[JsonIgnore]
	public IReadOnlyList<SteamMatch> SteamMatches { get; set; } = [];

	[JsonIgnore]
	public string Folder { get; set; } = "";

	public long FolderBytes { get; set; } = -1;

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? ManagedBroadcastAddress
	{
		get; set;
	}

	[JsonIgnore]
	public string FolderSize => FolderBytes < 0 ? "—" : $"{FolderBytes / 1_000_000_000d:N2} GB";

	[JsonIgnore]
	public bool IsPatched => SteamDllChecks is { } checks && SteamApiPaths is { Count: > 0 } paths &&
		paths.All(path => path != null && checks.TryGetValue(path, out var check) && check is { Patched: true });

	[JsonIgnore]
	public bool IsReady => IsNonSteam || IsPatched;

	[JsonIgnore]
	public string StatusMark => IsReady ? "✓" : SteamDllChecks == null ? "" : "✗";
}
