using System.Collections.ObjectModel;

namespace NqCandidateLevels.Core;

public sealed record SourceBar(DateTime StartUtc, double Open, double High, double Low, double Close, double Volume)
{
    public DateTime EndUtc => StartUtc.AddMinutes(1);
    public bool IsValid => StartUtc.Kind == DateTimeKind.Utc && StartUtc.Ticks % TimeSpan.TicksPerMinute == 0 &&
        double.IsFinite(Open) && double.IsFinite(High) && double.IsFinite(Low) && double.IsFinite(Close) && Low > 0 &&
        (double.IsNaN(Volume) || double.IsFinite(Volume) && Volume >= 0) &&
        High >= Low && Open >= Low && Open <= High && Close >= Low && Close <= High;
}

public sealed record SourceIdentity(string ConnectionId, string ContractId, string Name, DateTime ExpirationUtc, double TickSize);
public enum ManualBias { Both, BuyOnly, SellOnly, NoTrade }
public enum MechanicalSide { BreakoutBuy, BreakdownSell, AtLevel }
public enum LevelFamily { PreviousRth, PreviousFutures, Overnight, CurrentWeek, PreviousWeek, CurrentRth, OpeningRange, Structure, VolumeProfile, Options }
public enum ReferenceKind { Unknown, High, Low, Open, Close, VwapProxy, SwingHigh, SwingLow, Poc, Hvn, Lvn, OptionConcentration, OptionActivity, OptionContext }

[Flags]
public enum QualityFlags
{
    None = 0, Complete = 1, Partial = 2, UncertainSchedule = 4, Corrected = 8,
    VolumeUnavailable = 16, Stale = 32, MissingStart = 64, BarVwapProxy = 128,
    VerifiedUnderlying = 256, RealVolumeAtPrice = 512
}

public sealed record EngineSettings
{
    public int LookbackDays { get; init; } = 30;
    public bool PreviousRth { get; init; } = true;
    public bool PreviousFutures { get; init; } = true;
    public bool Overnight { get; init; } = true;
    public bool CurrentWeek { get; init; } = true;
    public bool PreviousWeek { get; init; } = true;
    public bool CurrentRth { get; init; } = true;
    public bool OpeningRange { get; init; } = true;
    public bool Structure { get; init; } = true;
    public bool ShowDevelopingOpeningRange { get; init; }
    public int OpeningRangeMinutes { get; init; } = 15;
    public int SwingLeftBars { get; init; } = 2;
    public int SwingRightBars { get; init; } = 2;
    public int SwingLookbackBars { get; init; } = 1440;
    public int SwingCountPerSide { get; init; } = 5;
    public double AtLevelToleranceTicks { get; init; } = 0.5;
}

/// <summary>One unmerged provider reference. Availability begins at KnownAtUtc, including swing confirmation.</summary>
public sealed record CandidateLevel
{
    public string Id { get; init; } = "";
    public long SnapshotVersion { get; init; }
    public string ConnectionId { get; init; } = "";
    public string ContractId { get; init; } = "";
    public LevelFamily Family { get; init; }
    public ReferenceKind Kind { get; init; }
    /// <summary>Observed source event, shared by aliases such as a session high and a confirmed swing high.</summary>
    public string OriginatingEventId { get; init; } = "";
    public string Label { get; init; } = "";
    public double RawPrice { get; init; }
    public double DisplayPrice { get; init; }
    public DateOnly TradingDate { get; init; }
    public DateTime IntervalStartUtc { get; init; }
    public DateTime IntervalEndUtc { get; init; }
    public DateTime OriginUtc { get; init; }
    public DateTime KnownAtUtc { get; init; }
    public DateTime ComputedAtUtc { get; init; }
    public DateTime LastUpdateUtc { get; init; }
    public bool Developing { get; init; }
    public bool Frozen { get; init; }
    public bool Inactive { get; init; }
    /// <summary>Explicit adapter evidence validity, separate from source health and manual eligibility.</summary>
    public bool EvidenceAvailable { get; init; } = true;
    public MechanicalSide Side { get; init; }
    public bool ManualPermission { get; init; }
    public QualityFlags Quality { get; init; }
    public string Provenance { get; init; } = "Independent NQZ6 completed M1 Last bars";
    public ProfilePoint? Profile { get; init; }
    public OptionStrike? Option { get; init; }
    public OptionConcentration? OptionConcentration { get; init; }

    public CandidateLevel Freeze() => this with
    {
        Profile = Profile?.Freeze(),
        Option = Option is null ? null : Option with
        {
            Contracts = Array.AsReadOnly(Option.Contracts.ToArray()),
            Concentrations = Array.AsReadOnly(Option.Concentrations.Select(x => x.Freeze()).ToArray()),
            StrongestConcentration = Option.StrongestConcentration?.Freeze()
        },
        OptionConcentration = OptionConcentration?.Freeze()
    };
}

public sealed record RenderLevel(double DisplayPrice, IReadOnlyList<CandidateLevel> References)
{
    public string Label => string.Join(" / ", References.Select(x => x.Label).Distinct());
    public DateTime KnownAtUtc => References.Min(x => x.KnownAtUtc);
    public bool HasActiveBuy => References.Any(x => !x.Inactive && x.ManualPermission && x.Side == MechanicalSide.BreakoutBuy);
    public bool HasActiveSell => References.Any(x => !x.Inactive && x.ManualPermission && x.Side == MechanicalSide.BreakdownSell);
    public bool Developing => References.Any(x => x.Developing);
}

public sealed record LevelSnapshot(
    SourceIdentity Source, long Version, DateTime AsOfUtc, DateTime ComputedAtUtc,
    DateTime? LastSourceUpdateUtc, double? LatestSourcePrice, bool SourceActive,
    ManualBias Bias, IReadOnlyList<CandidateLevel> References,
    IReadOnlyList<CandidateLevel> ActiveCandidates, IReadOnlyList<RenderLevel> Rendering,
    IReadOnlyDictionary<string, string> ProviderStatus)
{
    public OptionConcentrationResult? OptionAnalysis { get; init; }
    public IReadOnlyList<OptionContract> OptionContracts { get; init; } = Array.Empty<OptionContract>();
}

public static class SnapshotBuilder
{
    public static LevelSnapshot Build(SourceIdentity source, long version, DateTime asOfUtc, DateTime computedAtUtc,
        DateTime? sourceUpdateUtc, double? sourcePrice, bool sourceActive, ManualBias bias,
        IEnumerable<CandidateLevel> references, IReadOnlyDictionary<string, string>? status = null,
        double toleranceTicks = 0.5)
    {
        if (!(source.TickSize > 0) || !double.IsFinite(source.TickSize)) throw new ArgumentOutOfRangeException(nameof(source));
        var materialized = references.Where(x => double.IsFinite(x.RawPrice) && x.KnownAtUtc <= asOfUtc).Select(x =>
        {
            var difference = sourcePrice.HasValue ? x.RawPrice - sourcePrice.Value : 0;
            var side = Math.Abs(difference) <= source.TickSize * Math.Max(0, toleranceTicks)
                ? MechanicalSide.AtLevel : difference > 0 ? MechanicalSide.BreakoutBuy : MechanicalSide.BreakdownSell;
            var permission = side switch
            {
                MechanicalSide.BreakoutBuy => bias is ManualBias.Both or ManualBias.BuyOnly,
                MechanicalSide.BreakdownSell => bias is ManualBias.Both or ManualBias.SellOnly,
                _ => false
            };
            return x.Freeze() with { SnapshotVersion = version, ConnectionId = source.ConnectionId, ContractId = source.ContractId,
                DisplayPrice = NormalizePrice(x.RawPrice, source.TickSize), Side = side, ManualPermission = permission,
                Inactive = x.Inactive || !sourceActive || !sourcePrice.HasValue || x.Kind == ReferenceKind.OptionContext, ComputedAtUtc = computedAtUtc,
                Quality = sourceActive ? x.Quality : x.Quality | QualityFlags.Stale };
        }).OrderBy(x => x.DisplayPrice).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var render = materialized.GroupBy(x => PriceTick(x.DisplayPrice, source.TickSize))
            .Select(g => new RenderLevel(g.First().DisplayPrice, Array.AsReadOnly(g.ToArray()))).ToArray();
        var active = materialized.Where(x => !x.Inactive && x.ManualPermission).ToArray();
        return new LevelSnapshot(source, version, asOfUtc, computedAtUtc, sourceUpdateUtc, sourcePrice, sourceActive, bias,
            Array.AsReadOnly(materialized), Array.AsReadOnly(active), Array.AsReadOnly(render),
            new ReadOnlyDictionary<string, string>(status is null ? new Dictionary<string, string>() : new Dictionary<string, string>(status)));
    }

    public static long PriceTick(double price, double tickSize) => checked((long)Math.Round(price / tickSize, MidpointRounding.AwayFromZero));
    public static double NormalizePrice(double price, double tickSize) => PriceTick(price, tickSize) * tickSize;
}
