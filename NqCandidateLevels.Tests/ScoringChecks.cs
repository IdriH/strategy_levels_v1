using NqCandidateLevels.Core;

internal static class ScoringChecks
{
    private static readonly DateTime Now = new(2026, 10, 12, 14, 0, 0, DateTimeKind.Utc);
    private static readonly SourceIdentity Source = new("rithmic", "NQZ6", "NQZ6", SessionCalendar.NqZ6LastTradingUtc, .25);

    public static void Run()
    {
        HandCalculated();
        ClustersAndPermissions();
        AvailabilityAndCopies();
        ProfileProminence();
    }

    private static CandidateLevel Ref(string id, double price, LevelFamily family = LevelFamily.PreviousRth,
        ReferenceKind kind = ReferenceKind.High, string? origin = null) => new()
    {
        Id = id, Label = id, RawPrice = price, Family = family, Kind = kind, OriginatingEventId = origin ?? id,
        KnownAtUtc = Now.AddMinutes(-1), OriginUtc = Now.AddHours(-2), Quality = QualityFlags.Complete
    };

    private static LevelSnapshot Raw(IEnumerable<CandidateLevel> refs, double price = 90, ManualBias bias = ManualBias.Both,
        bool active = true, DateTime? computed = null) => SnapshotBuilder.Build(Source, 42, Now, computed ?? Now,
            Now, price, active, bias, refs);

    private static CandidateLevel[] Example()
    {
        var profile = ProfileGeometry.Calculate(new[] { new ProfileBin(100.25, 100),
            new ProfileBin(100.5, 100 * Math.Pow(2, .8)), new ProfileBin(100.75, 100) }, .25,
            new ProfileSettings(SmoothingRadius: 0), true).Points.Single(x => x.Kind == ProfileLevelKind.Poc);
        return new[]
        {
            Ref("session", 100),
            Ref("profile", 100.5, LevelFamily.VolumeProfile, ReferenceKind.Poc) with
                { Profile = profile, Quality = QualityFlags.Complete | QualityFlags.RealVolumeAtPrice },
            Ref("option", 100.75, LevelFamily.Options, ReferenceKind.OptionConcentration) with
            {
                Quality = QualityFlags.Complete | QualityFlags.VerifiedUnderlying,
                OptionConcentration = new()
                {
                    Id = "concentration", Mode = OptionDataMode.OpenInterestOnly, IsEligible = true, ConfirmedPeak = true,
                    EffectiveConcentration = .8, RawConcentration = 1, KnownAtUtc = Now.AddSeconds(-10),
                    RetrievalUtc = Now.AddSeconds(-10), ValidUntilUtc = Now.AddSeconds(290), ExpiryUtc = Now.AddDays(3)
                }
            }
        };
    }

    private static void HandCalculated()
    {
        var zone = LevelScoring.Build(Raw(Example())).Zones.Single();
        Near(38, zone.Score.Base, "RTH PDH base"); Near(18, zone.Score.Confluence, "three independent groups");
        Near(20, zone.Score.Options, "0.8 concentration"); Near(8, zone.Score.Profile, "0.8 real profile prominence");
        Near(84, zone.Score.Total, "hand-calculated importance"); Equal(ImportanceTier.A, zone.Tier, "84 A");
        var aliases = new[] { Ref("rth", 100, origin: "same-extreme"),
            Ref("week", 100, LevelFamily.CurrentWeek, ReferenceKind.High, "same-extreme"),
            Ref("swing", 100, LevelFamily.Structure, ReferenceKind.SwingHigh, "same-extreme") };
        var aliasZone = LevelScoring.Build(Raw(aliases)).Zones.Single();
        Near(38, aliasZone.Score.Total, "aliases must not manufacture confluence");
        Equal(ImportanceTier.C, aliasZone.Tier, "38 C"); Equal(3, aliasZone.Members.Count, "all aliases retained");
        Check(aliasZone.Score.Explanations.Any(x => x.Contains("aliases")), "explicit alias provenance");
        Near(38, LevelScoring.Build(Raw(new[] { aliases[0] })).Zones[0].Score.Total, "missing options/profile fixed denominator");
        Near(28.5, LevelScoring.Build(Raw(new[] { aliases[0] with { Developing = true } })).Zones[0].Score.Base, "per-member developing discount");
        Near(100, LevelScoring.Build(Raw(Example()), new() { BaseMaximum = 100 }).Zones[0].Score.Total, "clamp total at 100");
        Near(46, LevelScoring.Build(Raw(Example()), new() { BaseMaximum = 0 }).Zones[0].Score.Total, "zero base weight preserves other components");
        Equal(ImportanceTier.A, LevelScoring.TierFor(75), "A exact threshold");
        Equal(ImportanceTier.B, LevelScoring.TierFor(74.999), "below A");
        Equal(ImportanceTier.B, LevelScoring.TierFor(50), "B exact threshold");
        Equal(ImportanceTier.C, LevelScoring.TierFor(49.999), "below B");
        Equal(ImportanceTier.C, LevelScoring.TierFor(35), "C exact threshold");
        Equal(ImportanceTier.D, LevelScoring.TierFor(34.999), "below C");
        var renamed = Example().Select(x => x with { Label = "an arbitrary human label" });
        Near(84, LevelScoring.Build(Raw(renamed)).Zones[0].Score.Total, "labels never determine scoring");
        var copied = Example().Concat(new[] { Example()[1] with { Id = "profile-alias" }, Example()[2] with { Id = "option-expiry-alias" } });
        Near(84, LevelScoring.Build(Raw(copied)).Zones[0].Score.Total, "nodes and expiries never sum bonuses");
    }

    private static void ClustersAndPermissions()
    {
        var refs = new[] { Ref("a", 100), Ref("b", 102), Ref("c", 104) };
        var snapshot = LevelScoring.Build(Raw(refs));
        Equal(2, snapshot.Zones.Count, "complete-link has no transitive 4-point zone");
        Near(100, snapshot.Zones[0].LowPrice, "zone minimum"); Near(102, snapshot.Zones[0].HighPrice, "zone maximum");
        Near(100, snapshot.Zones[0].AnchorPrice, "stable ID tie picks actual reference");
        Equal(3, LevelScoring.Build(Raw(refs), new() { ZoneToleranceTicks = 0 }).Zones.Count, "zero tolerance exact only");
        Equal(snapshot.Zones[0].Id, LevelScoring.Build(Raw(refs.Reverse())).Zones[0].Id, "shuffle stable membership ID");
        var equalTicks = Raw(new[] { Ref("x", 100), Ref("y", 100.000000001) });
        Equal(1, equalTicks.Rendering.Count, "raw tick merge preserved");
        Equal(1, LevelScoring.Build(equalTicks, new() { ZoneToleranceTicks = 0 }).Zones.Count, "exact tick scoring merge");
        var straddling = LevelScoring.Build(Raw(refs.Take(2), 101)).Zones[0];
        Equal(MechanicalSide.AtLevel, straddling.Side, "whole band straddles price"); Check(straddling.Inactive, "straddling is watch");
        foreach (var bias in Enum.GetValues<ManualBias>())
        {
            var scored = LevelScoring.Build(Raw(new[] { Ref("buy", 110), Ref("sell", 80) }, 100, bias));
            Equal(bias is ManualBias.Both or ManualBias.BuyOnly ? 1 : 0, scored.ActiveBuyZones.Count, "buy bias");
            Equal(bias is ManualBias.Both or ManualBias.SellOnly ? 1 : 0, scored.ActiveSellZones.Count, "sell bias");
            Check(scored.Zones.All(x => x.Score.Total == 38), "bias never changes intrinsic importance");
        }
        var stale = LevelScoring.Build(Raw(Example(), active: false));
        Equal(0, stale.ActiveBuyZones.Count, "stale cannot activate a critical zone"); Near(84, stale.Zones[0].Score.Total, "source status distinct from intrinsic evidence");
        var context = Ref("option context", 110, LevelFamily.Options, ReferenceKind.OptionContext) with { Quality = QualityFlags.VerifiedUnderlying };
        var watch = LevelScoring.Build(Raw(new[] { context }));
        Near(10, watch.Zones[0].Score.Total, "unmeasured nearest base10"); Equal(0, watch.ActiveBuyZones.Count, "option context always inactive");
        Near(0, LevelScoring.Build(Raw(new[] { context with { EvidenceAvailable = false } })).Zones[0].Score.Total,
            "adapter-expired context cannot supply base even with frozen source as-of");
    }

    private static void AvailabilityAndCopies()
    {
        var example = Example();
        var delayed = example[2] with { OptionConcentration = example[2].OptionConcentration! with { KnownAtUtc = Now.AddSeconds(1) } };
        var score = LevelScoring.Build(Raw(example.Take(2).Append(delayed))).Zones[0];
        Near(0, score.Score.Options, "future metric unavailable"); Near(56, score.Score.Total, "future options cannot inflate group or base");
        var expired = example[2] with { OptionConcentration = example[2].OptionConcentration! with { ValidUntilUtc = Now.AddSeconds(-1) } };
        Near(0, LevelScoring.Build(Raw(new[] { expired })).Zones[0].Score.Total, "stale normalization context rejected");
        var future = example[0] with { KnownAtUtc = Now.AddSeconds(1) };
        Equal(0, LevelScoring.Build(Raw(new[] { future })).Zones.Count, "future reference absent");
        var computed = LevelScoring.Build(Raw(example, computed: Now.AddSeconds(5)));
        Check(computed.Zones[0].KnownAtUtc > Now, "newly computed score cannot style earlier viewport");
        Equal(42L, computed.SourceSnapshotVersion, "raw/scored coherent version");
        var names = new List<string> { "original" };
        var copyInput = example[2] with { OptionConcentration = example[2].OptionConcentration! with { ContractIds = names } };
        var frozen = LevelScoring.Build(Raw(new[] { copyInput })); names[0] = "changed";
        Equal("original", frozen.Zones[0].Members[0].OptionConcentration!.ContractIds[0], "nested options metadata copied");
        var bins = new List<ProfileBin> { new(100, 1) }; var smooth = new List<double> { 1 };
        var profile = example[1] with { Profile = example[1].Profile! with { Histogram = new(bins, smooth, 1, 1, 0, 1) } };
        var frozenProfile = LevelScoring.Build(Raw(new[] { profile })); bins[0] = new(100, 999); smooth[0] = 999;
        Near(1, frozenProfile.Zones[0].Members[0].Profile!.Histogram!.Bins[0].Volume, "profile bins copied");
        Near(1, frozenProfile.Zones[0].Members[0].Profile!.Histogram!.SmoothedVolumes[0], "profile smoothing copied");
        Check(computed.ToDiagnosticJson().Contains("importance-v1"), "serializable versioned export");
    }

    private static void ProfileProminence()
    {
        ProfileResult Profile(params double[] volume) => ProfileGeometry.Calculate(volume.Select((x, i) => new ProfileBin(100 + i * .25, x)),
            .25, new ProfileSettings(SmoothingRadius: 0, HighNodeLimit: 0), true);
        var flat = Profile(10, 10, 10, 10).Points.Single(); Near(0, flat.NormalizedProminence!.Value, "flat POC no bonus");
        var valley = Profile(1, 10, 2, 10, 1).Points.Single(x => x.Kind == ProfileLevelKind.Lvn);
        Near(.8, valley.NormalizedProminence!.Value, "independent interior peaks determine LVN prominence");
        Near(10, valley.LeftPeakSmoothedVolume!.Value, "full detected peak context retained despite HVN display limit0");
        var edgeFlanks = Profile(10, 2, 10).Points.Single(x => x.Kind == ProfileLevelKind.Lvn);
        Check(edgeFlanks.NormalizedProminence is null, "range edges are not fabricated independently identified local maxima");
        Near(24, valley.Histogram!.Bins.Sum(x => x.Volume), "raw volume conservation");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual, string message) => Check(Math.Abs(expected - actual) < 1e-8, $"{message}: expected {expected}, actual {actual}");
    private static void Equal<T>(T expected, T actual, string message) where T : notnull => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, actual {actual}");
}
