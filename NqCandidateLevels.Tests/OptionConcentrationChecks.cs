using NqCandidateLevels.Core;

internal static class OptionConcentrationChecks
{
    private static readonly DateTime Now = new(2026, 10, 12, 14, 0, 0, DateTimeKind.Utc);
    private static readonly SourceIdentity Source = new("rithmic", "NQZ6", "NQZ6", SessionCalendar.NqZ6LastTradingUtc, .25);

    public static void Run()
    {
        RankingAndModes(); MetricsAndCoverage(); PeaksAndUniverse();
    }

    private static OptionContract[] Chain(Func<int, double?>? value = null, int count = 12, int days = 3,
        bool oi = true, bool volume = false, bool knownPublication = false, bool explicitZero = false, string prefix = "")
    {
        value ??= i => i == 8 ? 1000 : i == 2 ? 500 : 100;
        return Enumerable.Range(0, count).SelectMany(i => new[] { OptionRight.Call, OptionRight.Put }.Select(right =>
        {
            var metric = value(i) / 2;
            var contract = new OptionContract($"{prefix}{days}:{i}:{right}", Source.ConnectionId, Source.ContractId,
                $"series{days}", Now.AddDays(days), 1000 + i * 10, right,
                oi ? metric : null, volume ? metric : null, Now, knownPublication ? Now.AddHours(-2) : null);
            return contract with
            {
                OpenInterestMetric = !oi ? null : new(metric,
                    explicitZero ? OptionMetricAvailability.SuppliedValid : OptionMetricAvailability.CachedValue,
                    Now, knownPublication ? Now.AddHours(-2) : null),
                VolumeMetric = !volume ? null : new(metric, OptionMetricAvailability.SuppliedValid, Now,
                    VolumeSessionId: "2026-10-12", VolumeSessionConfirmed: true)
            };
        })).ToArray();
    }

    private static OptionConcentrationResult Analyze(IEnumerable<OptionContract> rows, bool truncated = false,
        OptionConcentrationSettings? settings = null) => OptionConcentration.Analyze(rows, Source, 995, Now,
            settings, knownEntitlementTruncated: truncated);
    private static OptionConcentration Strong(OptionConcentrationResult result, int days = 3) =>
        result.Concentrations.Single(x => x.Strike == 1080 && x.ExpiryUtc == Now.AddDays(days));

    private static void RankingAndModes()
    {
        var chain = Chain(); var result = Analyze(chain);
        Equal(1080d, result.SelectedStrikes[0].Strike, "strong farther strike beats weaker nearer strike");
        Equal(1020d, result.SelectedStrikes[1].Strike, "weaker genuine peak remains second");
        Equal(2, result.SelectedStrikes.Count, "no forced third geometry slot");
        Near(1, Strong(result).RawConcentration, "hand-calculated strongest raw concentration");
        Near(.8, Strong(result).EffectiveConcentration, "OI-only capped .8");
        Near(.7, result.Concentrations.Single(x => x.Strike == 1020).RawConcentration, "0.6*0.5+0.4*1");
        Equal(OptionDataMode.OpenInterestOnly, Strong(result).Mode, "OI-only mode");
        var duplicate = Analyze(chain.Concat(chain));
        Near(1000, Strong(duplicate).OpenInterestTotal!.Value, "canonical call/put contracts counted once");
        Equal(2, Strong(duplicate).ContractCount, "call put contract count");
        var conflicting = Analyze(chain.Append(chain[16] with { Strike = 1090 }));
        Check(!Strong(conflicting).IsEligible && Strong(conflicting).OpenInterestTotal is null,
            "conflicting canonical contract cannot become duplicate evidence or a verified complete bucket");
        var volume = Analyze(Chain(oi: false, volume: true));
        Equal(OptionDataMode.VolumeOnly, Strong(volume).Mode, "volume-only activity mode"); Near(.5, Strong(volume).EffectiveConcentration, "volume cap .5");
        var both = Analyze(Chain(volume: true, knownPublication: true));
        Equal(OptionDataMode.Both, Strong(both).Mode, "both fields supplied"); Near(1, Strong(both).EffectiveConcentration, "both comparable known metrics full cap");
        Near(.8, Strong(Analyze(Chain(volume: true))).EffectiveConcentration, "unknown publication applies .8 cap with both metrics");
        var expiries = Analyze(Chain(days: 3).Concat(Chain(days: 14)).Concat(Chain(days: 45)));
        Near(.8, Strong(expiries, 3).EffectiveConcentration, "near expiry relevance");
        Near(.72, Strong(expiries, 14).EffectiveConcentration, "middle expiry relevance");
        Near(.64, Strong(expiries, 45).EffectiveConcentration, "far expiry relevance");
        Equal(3, expiries.AllStrikes.Single(x => x.Strike == 1080).Concentrations.Count, "per-expiry provenance retained");
        Near(.8, expiries.AllStrikes.Single(x => x.Strike == 1080).StrongestConcentration!.EffectiveConcentration, "max across expiries never sum");
        var lateStrong = Analyze(Chain(i => 100, days: 1).Concat(Chain(i => 100, days: 2)).Concat(Chain(days: 14)));
        Equal(1080d, lateStrong.SelectedStrikes[0].Strike, "ranking beyond two nearest expiries");
        var scaled = Analyze(Chain(i => i == 8 ? 1000000 : i == 2 ? 500000 : 100000, days: 14));
        Near(Strong(expiries, 14).RawConcentration, Strong(scaled, 14).RawConcentration, "per-expiry normalization scale invariant");
    }

    private static void MetricsAndCoverage()
    {
        Check(!Analyze(Chain(_ => 0)).HasUsableMetric, "cached default zero unknown");
        var zeros = Analyze(Chain(i => i == 0 ? 0 : i == 8 ? 1000 : 100, explicitZero: true));
        Near(0, zeros.Concentrations.Single(x => x.Strike == 1000).OpenInterestTotal!.Value, "genuine supplied zero retained");
        Near(1, Strong(zeros).OpenInterestCoverage, "explicit zero counts toward supplied bucket coverage");
        Check(!Analyze(Chain(_ => 0, explicitZero: true)).HasUsableMetric, "all-zero sample unavailable");
        var flat = Analyze(Chain(_ => 100));
        Check(flat.HasUsableMetric && flat.SelectedStrikes.Count == 0, "flat valid sample never fills proximity slots");
        Near(.6, Strong(flat).RawConcentration, "flat sample raw .6");
        Check(!Analyze(Chain(count: 9)).HasUsableMetric, "minimum ten distinct buckets");
        var unknownVolume = Chain(oi: false, volume: true).Select(x => x with { VolumeMetric = null });
        Check(!Analyze(unknownVolume).HasUsableMetric, "cached volume units do not establish volume session");
        var partial = Chain(count: 14, volume: true, knownPublication: true).Select(x => x.Strike >= 1120 ? x with
            { OpenInterest = null, OpenInterestMetric = null, TradedVolume = null, VolumeMetric = null } : x);
        var partialResult = Analyze(partial);
        Near(12d / 14, Strong(partialResult).OpenInterestCoverage, "returned-bucket coverage");
        Near(.8, Strong(partialResult).EffectiveConcentration, "70%-90% coverage cap");
        var insufficient = Analyze(Chain(count: 16).Select(x => x.Strike >= 1110 ? x with { OpenInterest = null, OpenInterestMetric = null } : x));
        Check(!insufficient.HasUsableMetric, "minimum count does not override <70% coverage");
        Check(!Analyze(Chain(), true).HasUsableMetric, "entitlement truncation refuses concentration");
        var oneRight = Analyze(Chain().Where(x => x.Right == OptionRight.Call));
        Check(!oneRight.HasUsableMetric, "one-sided call-only bucket not a full sample");
        Check(oneRight.Concentrations.Any(x => x.OpenInterestLowerBound > 0 && x.OpenInterestTotal is null), "partial lower-bound totals remain diagnostics");
        Check(!Analyze(Chain().Select(x => x with { ObservedUtc = Now.AddSeconds(1) })).HasUsableMetric, "future chain snapshot unavailable");
        Check(!Analyze(Chain().Select(x => x with { ObservedUtc = Now.AddSeconds(-301) })).HasUsableMetric, "chain TTL capped");
        Check(!Analyze(Chain().Select(x => x with { OpenInterestMetric = x.OpenInterestMetric! with { PublishedUtc = Now.AddSeconds(1) } })).HasUsableMetric, "future publication unavailable");
        var futureNormalizer = Analyze(Chain().Select(x => x.Strike == 1000 ? x with
        {
            OpenInterestMetric = x.OpenInterestMetric! with { Value = 1000000, ObservedUtc = Now.AddSeconds(1) }
        } : x));
        Near(1, Strong(futureNormalizer).RawConcentration, "future metric excluded from full-sample normalization");
        Equal(Now, Strong(futureNormalizer).KnownAtUtc, "invalid future normalizer cannot leak into availability");
        var observationTimes = Analyze(Chain().Select(x => x with
        {
            ObservedUtc = Now.AddSeconds(x.Strike == 1000 ? -1 : -10),
            OpenInterestMetric = x.OpenInterestMetric! with { ObservedUtc = Now.AddSeconds(x.Strike == 1000 ? -1 : -10) }
        }));
        Equal(Now.AddSeconds(-1), Strong(observationTimes).KnownAtUtc, "score depends on latest observation anywhere in normalization sample");
        Equal(Now.AddSeconds(290), Strong(observationTimes).ValidUntilUtc, "full sample validity bounded by earliest contributing metric TTL");
        var mixed = Analyze(Chain(knownPublication: true).Select((x, i) => i == 0 ? x with
            { OpenInterestMetric = x.OpenInterestMetric! with { ReferenceDate = new DateOnly(2026, 10, 9) } } : x));
        Check(!mixed.HasUsableMetric, "incomparable publication/reference dates excluded");
        var stalePublication = Analyze(Chain().Select(x => x with
            { OpenInterestMetric = x.OpenInterestMetric! with { ReferenceDate = new DateOnly(2026, 10, 5) } }));
        Check(!stalePublication.HasUsableMetric, "older than two completed trading dates excluded");
        var wrong = Analyze(Chain().Select(x => x with { UnderlyingContractId = "NQH7" }));
        Equal(0, wrong.ReturnedContractCount, "different futures expiry excluded");
        Check(Analyze(Chain(days: 3)).ReturnedContractCount == 24, "short-dated option on exact NQZ6 accepted");
    }

    private static void PeaksAndUniverse()
    {
        var plateau = Analyze(Chain(i => i is 7 or 8 ? 1000 : 100));
        Equal(1070d, plateau.SelectedStrikes.Single().Strike, "lower midpoint of equal peak plateau");
        var edge = Analyze(Chain(i => i == 0 ? 1000 : 100));
        Equal(0, edge.SelectedStrikes.Count, "sample edge not confirmed peak");
        var hole = Analyze(Chain(i => i == 7 ? null : i == 8 ? 1000 : 100));
        Check(hole.HasUsableMetric, "one unresolved bucket leaves adequate overall coverage");
        Equal(0, hole.SelectedStrikes.Count, "unresolved neighbor cannot confirm peak");
        var now = Now;
        var series = Enumerable.Range(1, 12).Select(i => new OptionSeriesCompatibilityMetadata
        {
            SeriesId = "s" + i, ConnectionId = Source.ConnectionId, Underlying = OptionUnderlyingEvidence.MatchesSource,
            Expiry = new OptionExpiryMetadata(now.AddDays(i * 5))
        }).ToArray();
        var universe = OptionUniverseSelection.Select(series, Source, Now);
        Equal(8, universe.SelectedSeries.Count, "bounded series scan");
        Check(universe.SelectedSeries.Any(x => x.SeriesId == "s12"), "latest valid expiry reserved");
        Check(universe.SelectedSeries.Take(4).Select(x => x.SeriesId).SequenceEqual(new[] { "s1", "s2", "s3", "s4" }), "nearest four reserved");
        Equal(string.Join(",", universe.SelectedSeries.Select(x => x.SeriesId)), string.Join(",",
            OptionUniverseSelection.Select(series.Reverse(), Source, Now).SelectedSeries.Select(x => x.SeriesId)), "universe shuffled determinism");
        var duplicates = OptionUniverseSelection.Select(series.Concat(series), Source, Now);
        Equal(string.Join(",", universe.SelectedSeries.Select(x => x.SeriesId)),
            string.Join(",", duplicates.SelectedSeries.Select(x => x.SeriesId)), "duplicate series IDs consume no additional budget");
        var conflictingSeries = OptionUniverseSelection.Select(series.Append(series[0] with { Expiry = new(Now.AddDays(2)) }), Source, Now);
        Check(conflictingSeries.SelectedSeries.All(x => x.SeriesId != "s1"), "conflicting duplicate series expiry fails closed");
        var sameDay = new OptionSeriesCompatibilityMetadata
        {
            SeriesId = "same-day-probe", ConnectionId = Source.ConnectionId,
            Underlying = OptionUnderlyingEvidence.MatchesSource, Expiry = new(Now.Date, true)
        };
        var probes = OptionUniverseSelection.Select(series.Append(sameDay), Source, Now);
        Equal(8, probes.SelectedSeries.Count, "same-day probes stay within budget");
        Equal(1, probes.SameDayProbeCount, "nearest same-day cutoff gets resolution priority");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual, string message) => Check(Math.Abs(expected - actual) < 1e-8, $"{message}: expected {expected}, actual {actual}");
    private static void Equal<T>(T expected, T actual, string message) where T : notnull => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, actual {actual}");
}
