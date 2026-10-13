using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NqCandidateLevels.Core;

public enum EvidenceGroup { SessionExtremes, WeeklyReferences, ConfirmedStructure, AuctionReferences, OptionsConcentration }
public enum ImportanceTier { A, B, C, D }

/// <summary>Versioned hypotheses for reference importance. These settings do not model trade outcome probabilities.</summary>
public sealed record ScoringSettings
{
    public const string CurrentVersion = "importance-v1";
    public string Version { get; init; } = CurrentVersion;
    public int ZoneToleranceTicks { get; init; } = 8;
    public double AtLevelToleranceTicks { get; init; } = 0.5;
    public double BaseMaximum { get; init; } = 40;
    public double ConfluenceMaximum { get; init; } = 25;
    public double OptionsMaximum { get; init; } = 25;
    public double ProfileMaximum { get; init; } = 10;
    public double CriticalThreshold { get; init; } = 75;
    public double MajorThreshold { get; init; } = 50;
    public double SecondaryThreshold { get; init; } = 35;
    public double DevelopingMultiplier { get; init; } = 0.75;
    public double OptionsGroupMinimum { get; init; } = 0.65;
    public int OptionRetrievalTtlSeconds { get; init; } = 300;

    public void Validate()
    {
        if (Version != CurrentVersion || ZoneToleranceTicks is < 0 or > 40 ||
            !Range(AtLevelToleranceTicks, 0, 40) || !Range(BaseMaximum, 0, 100) ||
            !Range(ConfluenceMaximum, 0, 100) || !Range(OptionsMaximum, 0, 100) || !Range(ProfileMaximum, 0, 100) ||
            !Range(SecondaryThreshold, 0, 100) || !Range(MajorThreshold, SecondaryThreshold, 100) ||
            !Range(CriticalThreshold, MajorThreshold, 100) || !Range(DevelopingMultiplier, 0, 1) ||
            !Range(OptionsGroupMinimum, 0, 1) || OptionRetrievalTtlSeconds is < 1 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(ScoringSettings), "Invalid versioned scoring settings.");
    }

    public string Hash => StableIdentity.Hash(string.Join("|", Version, ZoneToleranceTicks.ToString(CultureInfo.InvariantCulture),
        AtLevelToleranceTicks.ToString("R", CultureInfo.InvariantCulture), BaseMaximum.ToString("R", CultureInfo.InvariantCulture),
        ConfluenceMaximum.ToString("R", CultureInfo.InvariantCulture), OptionsMaximum.ToString("R", CultureInfo.InvariantCulture),
        ProfileMaximum.ToString("R", CultureInfo.InvariantCulture), CriticalThreshold.ToString("R", CultureInfo.InvariantCulture),
        MajorThreshold.ToString("R", CultureInfo.InvariantCulture), SecondaryThreshold.ToString("R", CultureInfo.InvariantCulture),
        DevelopingMultiplier.ToString("R", CultureInfo.InvariantCulture), OptionsGroupMinimum.ToString("R", CultureInfo.InvariantCulture),
        OptionRetrievalTtlSeconds.ToString(CultureInfo.InvariantCulture)));

    private static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}

public sealed record ScoreBreakdown(double Base, double Confluence, double Options, double Profile, double Total,
    IReadOnlyList<string> Explanations, IReadOnlyList<string> EvidenceIds);

public sealed record ScoredZone
{
    public string Id { get; init; } = "";
    public double LowPrice { get; init; }
    public double HighPrice { get; init; }
    public double AnchorPrice { get; init; }
    public IReadOnlyList<CandidateLevel> Members { get; init; } = Array.Empty<CandidateLevel>();
    public IReadOnlyList<EvidenceGroup> EvidenceGroups { get; init; } = Array.Empty<EvidenceGroup>();
    public ImportanceTier Tier { get; init; }
    public ScoreBreakdown Score { get; init; } = new(0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>());
    public IReadOnlyList<string> QualityBadges { get; init; } = Array.Empty<string>();
    public MechanicalSide Side { get; init; }
    public bool ManualPermission { get; init; }
    public bool Inactive { get; init; }
    public bool Developing { get; init; }
    public DateTime KnownAtUtc { get; init; }
    public double Distance { get; init; }
    public string ImportanceLabel => Tier switch { ImportanceTier.A => "Critical", ImportanceTier.B => "Major", ImportanceTier.C => "Secondary", _ => "Minor" };
    public int DefaultWidth => Tier switch { ImportanceTier.A => 4, ImportanceTier.B => 3, ImportanceTier.C => 2, _ => 1 };
}

/// <summary>One current scored view. Known-at also includes the publication of this view; it is not a historical archive.</summary>
public sealed record ScoredLevelSnapshot
{
    public SourceIdentity Source { get; init; } = null!;
    public long SourceSnapshotVersion { get; init; }
    public string ScoringVersion { get; init; } = ScoringSettings.CurrentVersion;
    public string SettingsHash { get; init; } = "";
    public ScoringSettings Settings { get; init; } = new();
    public DateTime AsOfUtc { get; init; }
    public DateTime KnownAtUtc { get; init; }
    public ManualBias Bias { get; init; }
    public bool SourceActive { get; init; }
    public double? SourcePrice { get; init; }
    public IReadOnlyList<ScoredZone> Zones { get; init; } = Array.Empty<ScoredZone>();
    public IReadOnlyList<ScoredZone> ActiveBuyZones { get; init; } = Array.Empty<ScoredZone>();
    public IReadOnlyList<ScoredZone> ActiveSellZones { get; init; } = Array.Empty<ScoredZone>();
    public IReadOnlyList<ScoredZone> WatchZones { get; init; } = Array.Empty<ScoredZone>();
    public IReadOnlyDictionary<string, string> ProviderDiagnostics { get; init; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    public OptionConcentrationResult? OptionAnalysis { get; init; }
    public IReadOnlyList<OptionContract> OptionContracts { get; init; } = Array.Empty<OptionContract>();

    /// <summary>Diagnostic DTO export. No connection settings or account credentials are included.</summary>
    public string ToDiagnosticJson(bool indented = true) => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = indented,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    });
}

internal static class StableIdentity
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
