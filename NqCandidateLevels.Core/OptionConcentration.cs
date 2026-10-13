using System.Globalization;

namespace NqCandidateLevels.Core;

public enum OptionMetricAvailability { Unknown, CachedValue, SuppliedValid }
public enum OptionDataMode { Unavailable, OpenInterestOnly, VolumeOnly, Both }

/// <summary>SuppliedValid is reserved for an interface confirming field validity (including
/// genuine zero). CachedValue cannot prove that default zero was populated.</summary>
public sealed record OptionMetricObservation(double? Value, OptionMetricAvailability Availability,
    DateTime ObservedUtc, DateTime? PublishedUtc = null, DateOnly? ReferenceDate = null,
    string? VolumeSessionId = null, bool VolumeSessionConfirmed = false);

public sealed record OptionConcentrationSettings
{
    public string Version { get; init; } = "importance-v1";
    public int SelectedStrikesEachSide { get; init; } = 3;
    public int ContextStrikesEachSide { get; init; } = 2;
    public bool ShowContextFallback { get; init; } = true;
    public int MinimumValidBuckets { get; init; } = 10;
    public double MinimumCoverage { get; init; } = .70;
    public double RawConcentrationThreshold { get; init; } = .65;
    public int CacheTtlSeconds { get; init; } = 300;
    public double MinimumBandHalfWidth { get; init; } = 500;
    public double MaximumBandHalfWidth { get; init; } = 2000;
    public double PreviousRthRangeMultiplier { get; init; } = 2;

    public void Validate()
    {
        if (Version != "importance-v1" || SelectedStrikesEachSide is < 1 or > 50 || ContextStrikesEachSide is < 0 or > 50 ||
            MinimumValidBuckets is < 3 or > 10000 || !double.IsFinite(MinimumCoverage) || MinimumCoverage is < .01 or > 1 ||
            !double.IsFinite(RawConcentrationThreshold) || RawConcentrationThreshold is < 0 or > 1 ||
            CacheTtlSeconds is < 1 or > 3600 || !double.IsFinite(MinimumBandHalfWidth) || MinimumBandHalfWidth <= 0 ||
            !double.IsFinite(MaximumBandHalfWidth) || MaximumBandHalfWidth < MinimumBandHalfWidth || MaximumBandHalfWidth > 2000 ||
            !double.IsFinite(PreviousRthRangeMultiplier) || PreviousRthRangeMultiplier < 0)
            throw new ArgumentException("Invalid importance-v1 option concentration settings.");
    }
}

/// <summary>One actual expiry/strike bucket, including lower-bound partial totals and rejection
/// diagnostics. Eligibility means a measured interior peak that meets the raw threshold.</summary>
public sealed record OptionConcentration
{
    public string Id { get; init; } = "";
    public DateTime ExpiryUtc { get; init; }
    public double Strike { get; init; }
    public long StrikeTick { get; init; }
    public OptionDataMode Mode { get; init; }
    public bool IsOpenInterestBacked => Mode is OptionDataMode.OpenInterestOnly or OptionDataMode.Both;
    public bool IsEligible { get; init; }
    public bool ConfirmedPeak { get; init; }
    public double RawConcentration { get; init; }
    public double EffectiveConcentration { get; init; }
    public double ExpiryWeight { get; init; }
    public double? OpenInterestTotal { get; init; }
    public double? VolumeTotal { get; init; }
    public double OpenInterestLowerBound { get; init; }
    public double VolumeLowerBound { get; init; }
    public double? CallOpenInterest { get; init; }
    public double? PutOpenInterest { get; init; }
    public double? CallVolume { get; init; }
    public double? PutVolume { get; init; }
    public double OpenInterestCoverage { get; init; }
    public double VolumeCoverage { get; init; }
    public int OpenInterestValidBuckets { get; init; }
    public int VolumeValidBuckets { get; init; }
    public int ReturnedBuckets { get; init; }
    public int ContractCount { get; init; }
    public bool OpenInterestPublicationUnknown { get; init; }
    public DateOnly? OpenInterestReferenceDate { get; init; }
    public string? VolumeSessionId { get; init; }
    public DateTime KnownAtUtc { get; init; }
    public DateTime RetrievalUtc { get; init; }
    public DateTime ValidUntilUtc { get; init; }
    public IReadOnlyList<string> ContractIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SeriesIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RejectionReasons { get; init; } = Array.Empty<string>();
    public OptionConcentration Freeze() => this with
    {
        ContractIds = Array.AsReadOnly(ContractIds.ToArray()), SeriesIds = Array.AsReadOnly(SeriesIds.ToArray()),
        RejectionReasons = Array.AsReadOnly(RejectionReasons.ToArray())
    };

    public static OptionConcentrationResult Analyze(IEnumerable<OptionContract> options, SourceIdentity source,
        double sourcePrice, DateTime asOfUtc, OptionConcentrationSettings? settings = null,
        double? previousRthRange = null, SessionCalendar? calendar = null, bool knownEntitlementTruncated = false)
        => OptionConcentrationAnalysis.Analyze(options, source, sourcePrice, asOfUtc, settings ?? new(),
            previousRthRange, calendar ?? new(), knownEntitlementTruncated);

    /// <summary>Only selects already analyzed strikes. No contracts, histories or metrics are scanned.</summary>
    public static OptionConcentrationResult Select(OptionConcentrationResult analysis, double sourcePrice,
        OptionConcentrationSettings? settings = null) => OptionConcentrationAnalysis.Select(analysis, sourcePrice, settings ?? new());
}

public sealed record OptionConcentrationResult
{
    public OptionConcentrationSettings Settings { get; init; } = new();
    public string SettingsHash => StableIdentity.Hash(System.Text.Json.JsonSerializer.Serialize(Settings));
    public string Status { get; init; } = "";
    public double BandLow { get; init; }
    public double BandHigh { get; init; }
    public DateTime AsOfUtc { get; init; }
    public DateTime KnownAtUtc { get; init; }
    public DateTime? ValidUntilUtc { get; init; }
    public bool HasUsableMetric { get; init; }
    public bool KnownEntitlementTruncated { get; init; }
    public int ScannedSeriesCount { get; init; }
    public int ReturnedContractCount { get; init; }
    public int ExcludedContractCount { get; init; }
    public IReadOnlyList<string> ScannedSeriesIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UnqueriedSeriesIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DateTime> ScannedExpiriesUtc { get; init; } = Array.Empty<DateTime>();
    public IReadOnlyList<OptionStrike> AllStrikes { get; init; } = Array.Empty<OptionStrike>();
    public IReadOnlyList<OptionStrike> SelectedStrikes { get; init; } = Array.Empty<OptionStrike>();
    public IReadOnlyList<OptionConcentration> Concentrations { get; init; } = Array.Empty<OptionConcentration>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public OptionConcentrationResult Freeze()
    {
        static OptionStrike Copy(OptionStrike strike) => strike with
        {
            Contracts = Array.AsReadOnly(strike.Contracts.ToArray()),
            Concentrations = Array.AsReadOnly(strike.Concentrations.Select(x => x.Freeze()).ToArray()),
            StrongestConcentration = strike.StrongestConcentration?.Freeze()
        };
        return this with
        {
            ScannedSeriesIds = Array.AsReadOnly(ScannedSeriesIds.ToArray()),
            UnqueriedSeriesIds = Array.AsReadOnly(UnqueriedSeriesIds.ToArray()),
            ScannedExpiriesUtc = Array.AsReadOnly(ScannedExpiriesUtc.ToArray()),
            AllStrikes = Array.AsReadOnly(AllStrikes.Select(Copy).ToArray()),
            SelectedStrikes = Array.AsReadOnly(SelectedStrikes.Select(Copy).ToArray()),
            Concentrations = Array.AsReadOnly(Concentrations.Select(x => x.Freeze()).ToArray()),
            Diagnostics = Array.AsReadOnly(Diagnostics.ToArray())
        };
    }
}

internal static class OptionConcentrationAnalysis
{
    private sealed record Metric(double? Value, DateTime KnownAtUtc, DateTime ObservedUtc,
        DateTime? PublishedUtc, DateOnly? ReferenceDate, string? SessionId, string? Rejection);
    private sealed record Bucket(long Tick, OptionContract[] Contracts, Metric[] Oi, Metric[] Volume,
        bool Rights, double? OiTotal, double? VolumeTotal);

    internal static OptionConcentrationResult Analyze(IEnumerable<OptionContract> input, SourceIdentity source,
        double price, DateTime asOf, OptionConcentrationSettings settings, double? previousRange,
        SessionCalendar calendar, bool truncated)
    {
        settings.Validate();
        if (asOf.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC as-of required.", nameof(asOf));
        if (!double.IsFinite(source.TickSize) || source.TickSize <= 0) throw new ArgumentException("Valid source tick required.");
        if (!double.IsFinite(price) || price <= 0) return new() { Status = "Options unavailable: valid source price required", AsOfUtc = asOf };
        var halfWidth = Math.Min(settings.MaximumBandHalfWidth, Math.Max(settings.MinimumBandHalfWidth,
            previousRange.HasValue && double.IsFinite(previousRange.Value) && previousRange > 0
                ? settings.PreviousRthRangeMultiplier * previousRange.Value : settings.MinimumBandHalfWidth));
        var rows = input.ToArray();
        bool Verified(OptionContract c) => c.ConnectionId == source.ConnectionId && c.UnderlyingContractId == source.ContractId &&
            !string.IsNullOrWhiteSpace(c.ContractId) && !string.IsNullOrWhiteSpace(c.SeriesId) &&
            c.Right is OptionRight.Call or OptionRight.Put && c.ExpiryUtc.Kind == DateTimeKind.Utc &&
            c.ExpiryUtc > asOf && c.ExpiryUtc <= source.ExpirationUtc && double.IsFinite(c.Strike) && c.Strike > 0 &&
            c.Strike >= price - halfWidth && c.Strike <= price + halfWidth &&
            Math.Abs(c.Strike / source.TickSize - Math.Round(c.Strike / source.TickSize)) < 1e-6 &&
            c.ObservedUtc is { Kind: DateTimeKind.Utc } observed && observed <= asOf;
        var diagnostics = new List<string>();
        var verified = rows.Where(Verified).GroupBy(c => c.ContractId, StringComparer.Ordinal).Select(g =>
        {
            // Prefer the latest available observation; conflicting canonical identities fail closed.
            if (g.Select(c => (c.ExpiryUtc, c.Strike, c.Right)).Distinct().Count() != 1) return null;
            var latest = g.Max(c => c.ObservedUtc);
            var candidates = g.Where(c => c.ObservedUtc == latest).Distinct().ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }).Where(c => c is not null).Cast<OptionContract>()
            .OrderBy(c => c.ExpiryUtc).ThenBy(c => c.Strike).ThenBy(c => c.ContractId, StringComparer.Ordinal).ToArray();
        var excluded = rows.Select(c => c.ContractId).Distinct(StringComparer.Ordinal).Count() - verified.Length;
        if (excluded > 0) diagnostics.Add($"{excluded} canonical contracts excluded: source/expiry/band/time or conflicting duplicate metadata");
        if (truncated) diagnostics.Add("Known entitlement-truncated response: no measured concentrations");
        diagnostics.Add("Coverage describes returned eligible band only; cached observations do not establish refresh or publication time");
        var result = new List<OptionConcentration>();
        foreach (var expiry in verified.GroupBy(c => c.ExpiryUtc))
        {
            var buckets = expiry.GroupBy(c => Tick(c.Strike, source.TickSize)).OrderBy(g => g.Key).Select(g =>
            {
                var contracts = g.OrderBy(c => c.ContractId, StringComparer.Ordinal).ToArray();
                var oi = contracts.Select(c => ReadMetric(c, false, asOf, settings, calendar)).ToArray();
                var volume = contracts.Select(c => ReadMetric(c, true, asOf, settings, calendar)).ToArray();
                var rights = contracts.Any(c => c.Right == OptionRight.Call) && contracts.Any(c => c.Right == OptionRight.Put);
                return new Bucket(g.Key, contracts, oi, volume, rights, Total(oi, rights), Total(volume, rights));
            }).ToArray();
            var oiDates = buckets.SelectMany(b => b.Oi).Where(m => m.Value.HasValue)
                .Select(m => m.ReferenceDate).Distinct().ToArray();
            var volumeSessions = buckets.SelectMany(b => b.Volume).Where(m => m.Value.HasValue)
                .Select(m => m.SessionId).Distinct(StringComparer.Ordinal).ToArray();
            var oiComparable = oiDates.Length <= 1;
            var volumeComparable = volumeSessions.Length <= 1;
            var oiValid = buckets.Count(b => b.OiTotal.HasValue);
            var volumeValid = buckets.Count(b => b.VolumeTotal.HasValue);
            var oiCoverage = oiValid / (double)buckets.Length;
            var volumeCoverage = volumeValid / (double)buckets.Length;
            var oiUsable = !truncated && oiComparable && oiValid >= settings.MinimumValidBuckets &&
                oiCoverage >= settings.MinimumCoverage && buckets.Any(b => b.OiTotal > 0);
            var volumeUsable = !truncated && volumeComparable && volumeValid >= settings.MinimumValidBuckets &&
                volumeCoverage >= settings.MinimumCoverage && buckets.Any(b => b.VolumeTotal > 0);
            var oiMetric = MetricConcentrations(buckets.Select(b => b.OiTotal).ToArray(), oiUsable);
            var volumeMetric = MetricConcentrations(buckets.Select(b => b.VolumeTotal).ToArray(), volumeUsable);
            var oiPeaks = Peaks(buckets.Select(b => b.OiTotal).ToArray(), oiUsable);
            var volumePeaks = Peaks(buckets.Select(b => b.VolumeTotal).ToArray(), volumeUsable);
            var mode = oiUsable ? volumeUsable ? OptionDataMode.Both : OptionDataMode.OpenInterestOnly :
                volumeUsable ? OptionDataMode.VolumeOnly : OptionDataMode.Unavailable;
            var publicationUnknown = oiUsable && oiDates.SingleOrDefault() is null;
            var expiryWeight = (expiry.Key - asOf).TotalDays <= 7 ? 1 : (expiry.Key - asOf).TotalDays <= 30 ? .9 : .8;
            var cap = mode switch { OptionDataMode.Both => 1, OptionDataMode.OpenInterestOnly => .8, OptionDataMode.VolumeOnly => .5, _ => 0 };
            if (publicationUnknown) cap = Math.Min(cap, .8);
            if (oiUsable && oiCoverage < .9 || volumeUsable && volumeCoverage < .9) cap = Math.Min(cap, .8);
            // Every expiry metric depends on the complete normalization/coverage sample.
            var allMetrics = buckets.SelectMany(b => b.Oi.Concat(b.Volume)).ToArray();
            var knownAt = expiry.Max(c => c.ObservedUtc!.Value);
            var metricKnownAt = allMetrics.Where(m => m.Value.HasValue).Select(m => m.KnownAtUtc).DefaultIfEmpty(knownAt).Max();
            knownAt = knownAt > metricKnownAt ? knownAt : metricKnownAt;
            var validity = allMetrics.Where(m => m.Value.HasValue).Select(m => m.ObservedUtc.AddSeconds(settings.CacheTtlSeconds))
                .Concat(expiry.Select(c => c.ObservedUtc!.Value.AddSeconds(settings.CacheTtlSeconds))).Append(expiry.Key).Min();
            for (var i = 0; i < buckets.Length; i++)
            {
                var b = buckets[i];
                var raw = mode switch
                {
                    OptionDataMode.Both when oiMetric[i].HasValue && volumeMetric[i].HasValue => .7 * oiMetric[i]!.Value + .3 * volumeMetric[i]!.Value,
                    OptionDataMode.OpenInterestOnly => oiMetric[i] ?? 0,
                    OptionDataMode.VolumeOnly => volumeMetric[i] ?? 0,
                    _ => 0
                };
                var peak = oiPeaks[i] || volumePeaks[i];
                var reasons = new List<string>();
                if (!b.Rights) reasons.Add("both call and put membership not returned");
                reasons.AddRange(b.Oi.Concat(b.Volume).Where(m => m.Rejection is not null).Select(m => m.Rejection!).Distinct(StringComparer.Ordinal));
                if (!oiComparable) reasons.Add("inconsistent OI publication/reference dates");
                if (!volumeComparable) reasons.Add("inconsistent confirmed volume sessions");
                if (!oiUsable) reasons.Add($"OI unavailable: {oiValid}/{buckets.Length} valid buckets; sample/coverage/positive/comparability requirements");
                if (!volumeUsable) reasons.Add($"volume unavailable: {volumeValid}/{buckets.Length} valid buckets; sample/coverage/positive/session requirements");
                if (truncated) reasons.Add("known entitlement truncation");
                if (!peak) reasons.Add("no confirmed interior local maximum (edge, flat plateau or unresolved neighbor)");
                if (raw < settings.RawConcentrationThreshold) reasons.Add("below raw concentration threshold");
                var eligible = mode != OptionDataMode.Unavailable && peak && raw >= settings.RawConcentrationThreshold && validity >= asOf;
                double? RightTotal(Metric[] metric, OptionRight right) => Total(metric.Where((_, n) => b.Contracts[n].Right == right).ToArray(), b.Contracts.Any(c => c.Right == right));
                result.Add(new OptionConcentration
                {
                    Id = $"opt:{source.ConnectionId}:{source.ContractId}:{expiry.Key.Ticks}:{b.Tick}", ExpiryUtc = expiry.Key,
                    StrikeTick = b.Tick, Strike = b.Tick * source.TickSize, Mode = mode, IsEligible = eligible,
                    ConfirmedPeak = peak, RawConcentration = raw, EffectiveConcentration = eligible ? expiryWeight * Math.Min(raw, cap) : 0,
                    ExpiryWeight = expiryWeight, OpenInterestTotal = b.OiTotal, VolumeTotal = b.VolumeTotal,
                    OpenInterestLowerBound = b.Oi.Sum(m => m.Value ?? 0), VolumeLowerBound = b.Volume.Sum(m => m.Value ?? 0),
                    CallOpenInterest = RightTotal(b.Oi, OptionRight.Call), PutOpenInterest = RightTotal(b.Oi, OptionRight.Put),
                    CallVolume = RightTotal(b.Volume, OptionRight.Call), PutVolume = RightTotal(b.Volume, OptionRight.Put),
                    OpenInterestCoverage = oiCoverage, VolumeCoverage = volumeCoverage, OpenInterestValidBuckets = oiValid,
                    VolumeValidBuckets = volumeValid, ReturnedBuckets = buckets.Length, ContractCount = b.Contracts.Length,
                    OpenInterestPublicationUnknown = publicationUnknown, OpenInterestReferenceDate = oiComparable ? oiDates.SingleOrDefault() : null,
                    VolumeSessionId = volumeComparable ? volumeSessions.SingleOrDefault() : null,
                    KnownAtUtc = knownAt, RetrievalUtc = b.Contracts.Max(c => c.ObservedUtc!.Value), ValidUntilUtc = validity,
                    ContractIds = Array.AsReadOnly(b.Contracts.Select(c => c.ContractId).ToArray()),
                    SeriesIds = Array.AsReadOnly(b.Contracts.Select(c => c.SeriesId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
                    RejectionReasons = Array.AsReadOnly(reasons.ToArray())
                });
            }
        }
        var byStrike = result.ToLookup(c => c.StrikeTick);
        var allStrikes = verified.GroupBy(c => Tick(c.Strike, source.TickSize)).OrderBy(g => g.Key).Select(g =>
        {
            var concentrations = byStrike[g.Key].OrderBy(c => c.ExpiryUtc).ToArray();
            return new OptionStrike(g.Key * source.TickSize, Array.AsReadOnly(g.ToArray()))
            {
                Concentrations = Array.AsReadOnly(concentrations),
                StrongestConcentration = Rank(concentrations.Where(c => c.IsEligible)).FirstOrDefault()
            };
        }).ToArray();
        var seriesIds = verified.Select(c => c.SeriesId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var analysis = new OptionConcentrationResult
        {
            Settings = settings,
            BandLow = price - halfWidth, BandHigh = price + halfWidth, AsOfUtc = asOf,
            KnownAtUtc = result.Select(c => c.KnownAtUtc).DefaultIfEmpty(asOf).Max(),
            ValidUntilUtc = result.Count == 0 ? null : result.Min(c => c.ValidUntilUtc),
            HasUsableMetric = result.Any(c => c.Mode != OptionDataMode.Unavailable), KnownEntitlementTruncated = truncated,
            ScannedSeriesCount = seriesIds.Length, ScannedSeriesIds = Array.AsReadOnly(seriesIds),
            ScannedExpiriesUtc = Array.AsReadOnly(verified.Select(c => c.ExpiryUtc).Distinct().Order().ToArray()),
            ReturnedContractCount = verified.Length, ExcludedContractCount = excluded,
            AllStrikes = Array.AsReadOnly(allStrikes), Concentrations = Array.AsReadOnly(result.ToArray()),
            Diagnostics = Array.AsReadOnly(diagnostics.ToArray())
        };
        return Select(analysis, price, settings);
    }

    internal static OptionConcentrationResult Select(OptionConcentrationResult analysis, double price, OptionConcentrationSettings settings)
    {
        settings.Validate();
        if (!double.IsFinite(price) || price <= 0) return analysis with { SelectedStrikes = Array.Empty<OptionStrike>(), Status = "Options unavailable: source price" };
        var eligible = analysis.AllStrikes.Where(s => s.StrongestConcentration?.IsEligible == true).ToArray();
        IOrderedEnumerable<OptionStrike> Ranked(IEnumerable<OptionStrike> strikes) => strikes
            .OrderByDescending(s => s.StrongestConcentration!.EffectiveConcentration)
            .ThenByDescending(s => s.StrongestConcentration!.RawConcentration).ThenBy(s => Math.Abs(s.Strike - price))
            .ThenBy(s => s.Strike).ThenBy(s => s.StrongestConcentration!.Id, StringComparer.Ordinal);
        var selected = Ranked(eligible.Where(s => s.Strike > price)).Take(settings.SelectedStrikesEachSide)
            .Concat(Ranked(eligible.Where(s => s.Strike < price)).Take(settings.SelectedStrikesEachSide))
            .Concat(eligible.Where(s => s.Strike == price)).ToArray();
        var status = analysis.HasUsableMetric ? selected.Length == 0 ? "Valid options sample: flat data or no qualifying confirmed peaks" :
            $"{selected.Length} measured strikes ranked by concentration" : "Unranked options data unavailable / insufficient sample";
        if (!analysis.HasUsableMetric && settings.ShowContextFallback)
            selected = analysis.AllStrikes.Where(s => s.Strike > price).OrderBy(s => s.Strike).Take(settings.ContextStrikesEachSide)
                .Concat(analysis.AllStrikes.Where(s => s.Strike < price).OrderByDescending(s => s.Strike).Take(settings.ContextStrikesEachSide)).ToArray();
        var oi = analysis.Concentrations.Select(c => c.OpenInterestCoverage).DefaultIfEmpty(0).Max();
        var volume = analysis.Concentrations.Select(c => c.VolumeCoverage).DefaultIfEmpty(0).Max();
        var modes = string.Join("/", selected.Select(s => s.StrongestConcentration?.Mode.ToString() ?? "context").Distinct(StringComparer.Ordinal));
        status += string.Create(CultureInfo.InvariantCulture, $"; {analysis.ScannedSeriesCount} scanned series / {analysis.AllStrikes.Count} strikes; band {analysis.BandLow:0.00}-{analysis.BandHigh:0.00}; best returned-bucket OI {oi:P0}, volume {volume:P0}; {modes}; TTL {settings.CacheTtlSeconds}s; cached/unknown publication unless explicitly supplied");
        return analysis with { SelectedStrikes = Array.AsReadOnly(selected), Status = status };
    }

    private static Metric ReadMetric(OptionContract contract, bool volume, DateTime asOf,
        OptionConcentrationSettings settings, SessionCalendar calendar)
    {
        var supplied = volume ? contract.VolumeMetric : contract.OpenInterestMetric;
        var observed = supplied?.ObservedUtc ?? contract.ObservedUtc!.Value;
        var publication = supplied?.PublishedUtc ?? (volume ? null : contract.OpenInterestPublishedUtc);
        var value = supplied is not null ? supplied.Value : volume ? contract.TradedVolume : contract.OpenInterest;
        var availability = supplied?.Availability ?? OptionMetricAvailability.CachedValue;
        var reference = supplied?.ReferenceDate ?? (publication.HasValue && publication.Value.Kind == DateTimeKind.Utc ? calendar.GetTradingDate(publication.Value) : null);
        string? rejection = null;
        if (!value.HasValue || !double.IsFinite(value.Value) || value < 0 || availability == OptionMetricAvailability.Unknown ||
            value == 0 && availability != OptionMetricAvailability.SuppliedValid) rejection = volume ? "volume missing/default/nonfinite" : "OI missing/default/nonfinite";
        else if (observed.Kind != DateTimeKind.Utc || observed > asOf || publication is { } pub && (pub.Kind != DateTimeKind.Utc || pub > asOf)) rejection = "metric unavailable as-of/future timestamp";
        else if ((asOf - observed).TotalSeconds > settings.CacheTtlSeconds ||
            (asOf - contract.ObservedUtc!.Value).TotalSeconds > settings.CacheTtlSeconds) rejection = "stale retrieval/metric observation";
        else if (volume && (supplied?.VolumeSessionConfirmed != true || string.IsNullOrWhiteSpace(supplied.VolumeSessionId))) rejection = "volume session unconfirmed";
        else if (!volume && reference.HasValue && !FreshReferenceDate(reference.Value, asOf, calendar)) rejection = "OI publication/reference date stale, future or uncertain";
        var knownAt = observed;
        if (publication > knownAt) knownAt = publication.Value;
        return new(rejection is null ? value : null, knownAt, observed, publication, reference, supplied?.VolumeSessionId, rejection);
    }

    private static bool FreshReferenceDate(DateOnly reference, DateTime asOf, SessionCalendar calendar)
    {
        var current = calendar.GetTradingDate(asOf);
        if (reference > current || current.DayNumber - reference.DayNumber > 14) return false;
        var completed = 0;
        for (var date = reference; date <= current; date = date.AddDays(1))
        {
            var schedule = calendar.GetSession(date);
            if (!schedule.IsCertain) return false;
            if (date > reference && schedule.IsTradingDay && schedule.FuturesEndUtc <= asOf) completed++;
        }
        return completed <= 2;
    }

    private static long Tick(double price, double size) => checked((long)Math.Round(price / size, MidpointRounding.AwayFromZero));
    private static double? Total(Metric[] values, bool membership) => membership && values.Length > 0 && values.All(v => v.Value.HasValue) &&
        double.IsFinite(values.Sum(v => v.Value!.Value)) ? values.Sum(v => v.Value!.Value) : null;
    private static IOrderedEnumerable<OptionConcentration> Rank(IEnumerable<OptionConcentration> values) => values
        .OrderByDescending(c => c.EffectiveConcentration).ThenByDescending(c => c.RawConcentration)
        .ThenBy(c => c.ExpiryUtc).ThenBy(c => c.Id, StringComparer.Ordinal);

    private static double?[] MetricConcentrations(double?[] values, bool usable)
    {
        if (!usable) return new double?[values.Length];
        var positive = values.Where(v => v > 0).Select(v => v!.Value).Order().ToArray();
        var maximum = positive[^1];
        var median = positive.Length % 2 == 1 ? positive[positive.Length / 2] :
            positive[positive.Length / 2 - 1] / 2 + positive[positive.Length / 2] / 2;
        return values.Select(v => !v.HasValue ? (double?)null : v == 0 ? 0 :
            .60 * (v.Value / maximum) + .40 * Math.Clamp(Math.Log2(v.Value / median), 0, 1)).ToArray();
    }

    private static bool[] Peaks(double?[] values, bool usable)
    {
        var peaks = new bool[values.Length];
        if (!usable) return peaks;
        for (var start = 0; start < values.Length;)
        {
            var end = start;
            while (end + 1 < values.Length && values[start].HasValue && values[end + 1] == values[start]) end++;
            if (start > 0 && end + 1 < values.Length && values[start] > 0 && values[start - 1].HasValue &&
                values[end + 1].HasValue && values[start] > values[start - 1] && values[start] > values[end + 1])
                peaks[(start + end) / 2] = true;
            start = end + 1;
        }
        return peaks;
    }
}
