using System.Collections.ObjectModel;
using System.Globalization;

namespace NqCandidateLevels.Core;

/// <summary>Pure, deterministic importance-v1. Explicit as-of availability; no ambient clock, data access, or label parsing.</summary>
public static class LevelScoring
{
    public static ScoredLevelSnapshot Build(LevelSnapshot raw, ScoringSettings? settings = null, DateTime? asOfUtc = null)
    {
        settings ??= new ScoringSettings();
        settings.Validate();
        var asOf = asOfUtc ?? raw.AsOfUtc;
        if (asOf.Kind != DateTimeKind.Utc || raw.ComputedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Score availability times must be UTC.");
        var price = raw.LastSourceUpdateUtc > asOf ? null : raw.LatestSourcePrice;
        if (price.HasValue && (!double.IsFinite(price.Value) || price <= 0)) price = null;
        var knownAt = new[] { asOf, raw.ComputedAtUtc }.Max();
        var members = raw.References.Where(x => x.KnownAtUtc <= asOf && x.KnownAtUtc.Kind == DateTimeKind.Utc)
            .Select(x => x.Freeze()).ToArray();
        var zones = LevelClustering.Build(members, raw.Source.TickSize, settings.ZoneToleranceTicks)
            .Select(x => ScoreZone(x, raw, settings, asOf, knownAt, price)).ToArray();
        IOrderedEnumerable<ScoredZone> Rank(IEnumerable<ScoredZone> items) => items.OrderByDescending(x => x.Score.Total)
            .ThenBy(x => x.Distance).ThenBy(x => x.LowPrice).ThenBy(x => x.Id, StringComparer.Ordinal);
        return new ScoredLevelSnapshot
        {
            Source = raw.Source, SourceSnapshotVersion = raw.Version, ScoringVersion = settings.Version,
            SettingsHash = StableIdentity.Hash(settings.Hash + "|" + (raw.OptionAnalysis?.SettingsHash ?? "options-disabled")),
            Settings = settings, AsOfUtc = asOf, KnownAtUtc = zones.Select(x => x.KnownAtUtc).DefaultIfEmpty(knownAt).Max(),
            SourceActive = raw.SourceActive && price.HasValue, SourcePrice = price, Bias = raw.Bias,
            Zones = Array.AsReadOnly(zones),
            ActiveBuyZones = Array.AsReadOnly(Rank(zones.Where(x => !x.Inactive && x.ManualPermission && x.Side == MechanicalSide.BreakoutBuy)).ToArray()),
            ActiveSellZones = Array.AsReadOnly(Rank(zones.Where(x => !x.Inactive && x.ManualPermission && x.Side == MechanicalSide.BreakdownSell)).ToArray()),
            WatchZones = Array.AsReadOnly(Rank(zones.Where(x => x.Inactive || !x.ManualPermission)).ToArray()),
            ProviderDiagnostics = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(raw.ProviderStatus)),
            OptionAnalysis = raw.OptionAnalysis?.Freeze(), OptionContracts = Array.AsReadOnly(raw.OptionContracts.ToArray())
        };
    }

    public static ImportanceTier TierFor(double score, ScoringSettings? settings = null)
    {
        settings ??= new ScoringSettings();
        settings.Validate();
        return score >= settings.CriticalThreshold ? ImportanceTier.A : score >= settings.MajorThreshold ? ImportanceTier.B :
            score >= settings.SecondaryThreshold ? ImportanceTier.C : ImportanceTier.D;
    }

    public static double BaseWeight(CandidateLevel member, DateTime asOfUtc, ScoringSettings? settings = null)
    {
        settings ??= new ScoringSettings();
        if (!Valid(member, asOfUtc)) return 0;
        var extreme = member.Kind is ReferenceKind.High or ReferenceKind.Low;
        var weight = member.Kind switch
        {
            ReferenceKind.OptionConcentration => ValidOption(member, asOfUtc, settings) && member.OptionConcentration!.IsOpenInterestBacked ? 30 : 0,
            ReferenceKind.OptionActivity => ValidOption(member, asOfUtc, settings) && member.OptionConcentration!.Mode == OptionDataMode.VolumeOnly ? 24 : 0,
            ReferenceKind.OptionContext => member.Quality.HasFlag(QualityFlags.VerifiedUnderlying) ? 10 : 0,
            ReferenceKind.Poc => GenuineProfile(member) ? 32 : 0,
            ReferenceKind.Hvn or ReferenceKind.Lvn => GenuineProfile(member) ? 28 : 0,
            ReferenceKind.SwingHigh or ReferenceKind.SwingLow when member.Family == LevelFamily.Structure => 24,
            ReferenceKind.Open when member.Family == LevelFamily.CurrentWeek => 26,
            ReferenceKind.Open when member.Family == LevelFamily.CurrentRth => 20,
            ReferenceKind.Close when member.Family == LevelFamily.PreviousRth => 24,
            ReferenceKind.Close when member.Family == LevelFamily.PreviousFutures => 22,
            ReferenceKind.VwapProxy when member.Family == LevelFamily.CurrentRth => 18,
            _ when extreme => member.Family switch
            {
                LevelFamily.PreviousWeek => 40,
                LevelFamily.PreviousRth => 38,
                LevelFamily.CurrentWeek or LevelFamily.PreviousFutures => 36,
                LevelFamily.Overnight => 32,
                LevelFamily.OpeningRange => 30,
                _ => 0
            },
            _ => 0
        };
        return weight * (member.Developing ? settings.DevelopingMultiplier : 1) * settings.BaseMaximum / 40;
    }

    private static ScoredZone ScoreZone(IReadOnlyList<CandidateLevel> members, LevelSnapshot raw, ScoringSettings settings,
        DateTime asOf, DateTime computedKnownAt, double? price)
    {
        var intrinsicSettings = settings with { BaseMaximum = 40 };
        var weighted = members.Select(x => (Member: x, Weight: BaseWeight(x, asOf, settings), Strength: BaseWeight(x, asOf, intrinsicSettings)))
            .OrderByDescending(x => x.Weight).ThenBy(x => x.Member.Id, StringComparer.Ordinal).ToArray();
        var valid = weighted.Where(x => x.Strength > 0).ToArray();
        // Deduplicate real originating events before counting rule families. All original aliases remain members.
        var events = valid.GroupBy(x => string.IsNullOrEmpty(x.Member.OriginatingEventId) ? "reference:" + x.Member.Id : "event:" + x.Member.OriginatingEventId,
                StringComparer.Ordinal).Select(g => g.OrderByDescending(x => x.Strength).ThenBy(x => x.Member.Id, StringComparer.Ordinal).First()).ToArray();
        var groups = events.Select(x => GroupFor(x.Member, asOf, settings)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().Order().ToArray();
        var anchor = valid.Length > 0 ? valid[0] : weighted[0];
        var basePoints = anchor.Weight;
        var confluence = (groups.Length switch { < 2 => 0, 2 => 10, 3 => 18, _ => 25 }) * settings.ConfluenceMaximum / 25;
        var optionMembers = valid.Where(x => ValidOption(x.Member, asOf, settings)).Select(x => x.Member).ToArray();
        var optionPoints = settings.OptionsMaximum * optionMembers.Select(x => x.OptionConcentration!.EffectiveConcentration).DefaultIfEmpty(0).Max();
        var profilePoints = settings.ProfileMaximum * valid.Where(x => GenuineProfile(x.Member)).Select(x => ProfileProminence(x.Member.Profile!)).DefaultIfEmpty(0).Max();
        var total = Math.Clamp(basePoints + confluence + optionPoints + profilePoints, 0, 100);
        var low = members.Min(x => x.RawPrice);
        var high = members.Max(x => x.RawPrice);
        var tolerance = settings.AtLevelToleranceTicks * raw.Source.TickSize;
        var side = !price.HasValue || !(low > price.Value + tolerance || high < price.Value - tolerance)
            ? MechanicalSide.AtLevel : low > price.Value + tolerance ? MechanicalSide.BreakoutBuy : MechanicalSide.BreakdownSell;
        var permission = side switch
        {
            MechanicalSide.BreakoutBuy => raw.Bias is ManualBias.Both or ManualBias.BuyOnly,
            MechanicalSide.BreakdownSell => raw.Bias is ManualBias.Both or ManualBias.SellOnly,
            _ => false
        };
        var inactive = !raw.SourceActive || !price.HasValue || side == MechanicalSide.AtLevel ||
            !valid.Any(x => !x.Member.Inactive && x.Member.Kind != ReferenceKind.OptionContext);
        var badges = new List<string>();
        if (!raw.SourceActive) badges.Add("STALE / SOURCE INACTIVE");
        if (members.Any(x => x.Family == LevelFamily.Options && x.Quality.HasFlag(QualityFlags.Stale))) badges.Add("STALE OPTIONS");
        if (members.Any(x => x.OptionConcentration?.OpenInterestPublicationUnknown == true)) badges.Add("CACHED OI / PUBLICATION UNKNOWN");
        if (members.Any(x => x.Developing)) badges.Add("DEVELOPING");
        if (members.Any(x => x.Quality.HasFlag(QualityFlags.Corrected))) badges.Add("CORRECTED");
        if (members.Any(x => x.Kind == ReferenceKind.OptionContext)) badges.Add("UNRANKED OPTIONS CONTEXT");
        foreach (var mode in optionMembers.Select(x => x.OptionConcentration!.Mode).Distinct().Order())
            badges.Add(mode switch { OptionDataMode.Both => "OI + VOLUME", OptionDataMode.OpenInterestOnly => "OI ONLY", _ => "VOLUME ACTIVITY" });
        if (inactive || !permission) badges.Add(side == MechanicalSide.AtLevel ? "AT LEVEL / WATCH" : "INACTIVE / WATCH");
        var reasons = new List<string>
        {
            $"Base {F(basePoints)}: {anchor.Member.Label}; strongest valid member",
            $"Confluence {F(confluence)}: {groups.Length} distinct groups after originating-event deduplication",
            $"Options {F(optionPoints)}: strongest effective measured concentration only",
            $"Profile {F(profilePoints)}: strongest validated node prominence only"
        };
        foreach (var aliases in valid.GroupBy(x => x.Member.OriginatingEventId, StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1))
            reasons.Add("Origin aliases retained once: " + string.Join(" / ", aliases.Select(x => x.Member.Label).Order(StringComparer.Ordinal)));
        var knownAt = valid.Select(x => x.Member.KnownAtUtc)
            .Concat(optionMembers.Select(x => x.OptionConcentration!.KnownAtUtc)).Append(computedKnownAt).Max();
        return new ScoredZone
        {
            Id = LevelClustering.ZoneId(members), LowPrice = low, HighPrice = high, AnchorPrice = anchor.Member.RawPrice,
            Members = members, EvidenceGroups = Array.AsReadOnly(groups), Tier = TierFor(total, settings),
            Score = new ScoreBreakdown(basePoints, confluence, optionPoints, profilePoints, total, Array.AsReadOnly(reasons.ToArray()),
                Array.AsReadOnly(valid.Select(x => x.Member.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())),
            QualityBadges = Array.AsReadOnly(badges.ToArray()), Side = side, ManualPermission = permission, Inactive = inactive,
            Developing = members.Any(x => x.Developing), KnownAtUtc = knownAt,
            Distance = !price.HasValue ? double.NaN : price < low ? low - price.Value : price > high ? price.Value - high : 0
        };
    }

    private static bool Valid(CandidateLevel member, DateTime asOf) => member.EvidenceAvailable && double.IsFinite(member.RawPrice) && member.RawPrice > 0 &&
        member.KnownAtUtc.Kind == DateTimeKind.Utc && member.KnownAtUtc <= asOf &&
        (member.Quality & (QualityFlags.UncertainSchedule | QualityFlags.Partial | QualityFlags.MissingStart)) == 0;

    private static bool ValidOption(CandidateLevel member, DateTime asOf, ScoringSettings settings) =>
        member.Kind is ReferenceKind.OptionConcentration or ReferenceKind.OptionActivity &&
        member.Quality.HasFlag(QualityFlags.VerifiedUnderlying) && member.OptionConcentration is { IsEligible: true } option &&
        option.KnownAtUtc <= asOf && option.ExpiryUtc > asOf && option.ValidUntilUtc >= asOf &&
        option.KnownAtUtc.Kind == DateTimeKind.Utc && option.RetrievalUtc.Kind == DateTimeKind.Utc && option.RetrievalUtc <= asOf &&
        (asOf - option.RetrievalUtc).TotalSeconds <= settings.OptionRetrievalTtlSeconds &&
        double.IsFinite(option.EffectiveConcentration) && option.EffectiveConcentration is >= 0 and <= 1;

    private static bool GenuineProfile(CandidateLevel member) => member.Family == LevelFamily.VolumeProfile &&
        member.Kind is ReferenceKind.Poc or ReferenceKind.Hvn or ReferenceKind.Lvn && member.Profile is not null &&
        member.Quality.HasFlag(QualityFlags.RealVolumeAtPrice);

    private static double ProfileProminence(ProfilePoint point)
    {
        if (point.Histogram is null || point.SmoothedVolume is not double node || !double.IsFinite(node) || node < 0) return 0;
        if (point.Kind == ProfileLevelKind.Lvn)
        {
            if (point.LeftPeakPrice is not double leftPrice || point.RightPeakPrice is not double rightPrice ||
                !(leftPrice < point.Price && rightPrice > point.Price) ||
                point.LeftPeakSmoothedVolume is not double left || point.RightPeakSmoothedVolume is not double right ||
                !double.IsFinite(left) || !double.IsFinite(right) || Math.Min(left, right) <= 0) return 0;
            return Math.Clamp(1 - node / Math.Min(left, right), 0, 1);
        }
        var median = point.Histogram.MedianPositiveSmoothedVolume;
        return node > 0 && double.IsFinite(median) && median > 0 ? Math.Clamp(Math.Log2(node / median), 0, 1) : 0;
    }

    private static EvidenceGroup? GroupFor(CandidateLevel member, DateTime asOf, ScoringSettings settings)
    {
        if (member.Kind == ReferenceKind.OptionConcentration)
            return ValidOption(member, asOf, settings) && member.OptionConcentration!.IsOpenInterestBacked &&
                member.OptionConcentration.EffectiveConcentration >= settings.OptionsGroupMinimum ? EvidenceGroup.OptionsConcentration : null;
        if (member.Kind is ReferenceKind.OptionActivity or ReferenceKind.OptionContext) return null;
        if (member.Family is LevelFamily.CurrentWeek or LevelFamily.PreviousWeek) return EvidenceGroup.WeeklyReferences;
        if (member.Family == LevelFamily.Structure) return EvidenceGroup.ConfirmedStructure;
        if (member.Kind is ReferenceKind.High or ReferenceKind.Low &&
            member.Family is LevelFamily.PreviousRth or LevelFamily.PreviousFutures or LevelFamily.Overnight or LevelFamily.OpeningRange)
            return EvidenceGroup.SessionExtremes;
        if (GenuineProfile(member) || member.Kind is ReferenceKind.Open or ReferenceKind.Close or ReferenceKind.VwapProxy)
            return EvidenceGroup.AuctionReferences;
        return null;
    }

    private static string F(double number) => number.ToString("0.##", CultureInfo.InvariantCulture);
}
