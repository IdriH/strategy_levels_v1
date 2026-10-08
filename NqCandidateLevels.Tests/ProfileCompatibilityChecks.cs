using NqCandidateLevels.Core;

internal static class ProfileCompatibilityChecks
{
    public static void Run()
    {
        var source = new SourceIdentity("rithmic-1", "NQZ6-contract", "NQZ6", SessionCalendar.NqZ6LastTradingUtc, 0.25);
        var start = new DateTime(2026, 10, 7, 13, 30, 0, DateTimeKind.Utc);
        ProfileMinuteData Minute(int index, double? bar = 10, double? native = 20,
            IReadOnlyList<ProfileBin>? bins = null) => new(start.AddMinutes(index), 100, 100.5, bar, native,
                bins ?? [new(100, 5), new(100.25, 15), new(100.5, 0)]);
        ProfileCoverageResult Validate(params ProfileMinuteData[] data) =>
            ProfileCoverageValidation.Validate(source, source, start, start.AddMinutes(2), data, ProfileDataOrigin.FeedNativeLast);
        void Reject(ProfileMinuteData minute, string description) =>
            Check(!Validate(minute, Minute(1)).Complete, description);

        var retained = Validate(Minute(0), Minute(1));
        Check(retained.Complete, "A self-consistent native profile must not be rejected because OHLCV volume differs");
        Check(retained.NativeTotalVolume == 40 && retained.BarTotalVolume == 20 && retained.BarVolumeMismatchCount == 2,
            "Both unmodified totals and mismatch count remain available");
        Check(retained.Bins.Sum(b => b.Volume) == 40 && retained.Bins.Single(b => b.Price == 100.25).Volume == 30,
            "Every native volume survives without division/rescaling");
        Check(retained.Bins.Single(b => b.Price == 100.5).Volume == 0, "Observed zero bins remain in the raw histogram");
        Check(retained.Status.Contains("OHLCV/native") && retained.Status.Contains("preserved"), "Disagreement is explicitly diagnostic");
        var geometry = ProfileGeometry.Calculate(retained.Bins, 0.25, new(SmoothingRadius: 0), retained.Complete);
        Check(geometry.Available && geometry.Points.Single(p => p.Kind == ProfileLevelKind.Poc).Price == 100.25,
            "Verified native volume yields its unchanged true-volume POC");
        var equal = Validate(Minute(0, bar: 20), Minute(1, bar: 20));
        Check(equal.Complete && equal.BarVolumeMismatchCount == 0, "Equivalent totals remain supported");
        var reordered = Validate(Minute(1), Minute(0));
        Check(reordered.Complete && reordered.Bins.Sum(b => b.Volume) == 40, "Input order does not change source-minute coverage");
        var unknownBar = Validate(Minute(0, bar: null), Minute(1, bar: double.NaN));
        Check(unknownBar.Complete && unknownBar.BarTotalVolume is null && unknownBar.BarVolumeUnavailableCount == 2,
            "A missing OHLCV metric cannot suppress independently verified native traded volume or fabricate a zero");
        var varyingDisagreement = Validate(Minute(0, bar: 7), Minute(1, bar: 13));
        Check(varyingDisagreement.Complete && varyingDisagreement.Bins.Sum(b => b.Volume) == 40,
            "Native conservation does not assume an undocumented constant OHLCV multiplier");

        Reject(Minute(0, native: 10), "Partial native price levels must fail even when OHLCV happens to match the native total");
        Reject(Minute(0, native: null), "Absent native Total.Volume is unavailable");
        Reject(Minute(0, native: double.NaN), "Undefined native total is unavailable");
        Reject(Minute(0, native: -1), "Negative native total sentinel is unavailable");
        Reject(Minute(0, native: 0, bins: Array.Empty<ProfileBin>()), "A native zero conflicts with known positive bar traded volume");
        Reject(Minute(0) with { PriceLevels = null }, "Missing positive native price-level coverage must fail");
        Reject(Minute(0, bins: Array.Empty<ProfileBin>()), "An empty profile with positive native volume must fail");
        Reject(Minute(0, bins: [new(100.10, 20)]), "Off-tick price levels must fail");
        Reject(Minute(0, bins: [new(100.75, 20)]), "Price levels outside the source Last bar must fail");
        Reject(Minute(0, bins: [new(100.25, -1)]), "Negative price-level volume must fail");
        Reject(Minute(0, bins: [new(100.25, double.PositiveInfinity)]), "Non-finite price-level volume must fail");
        Reject(Minute(0) with { Low = double.NaN }, "Missing Last price bounds must fail");
        Reject(Minute(0) with { High = 99 }, "Inverted Last price bounds must fail");
        Reject(Minute(0) with { StartUtc = DateTime.SpecifyKind(start, DateTimeKind.Unspecified) }, "Source minute UTC must be established");
        Check(!Validate(Minute(0)).Complete, "Missing minute coverage must fail");
        Check(!Validate(Minute(0), Minute(0)).Complete, "Duplicate minute bars cannot fabricate complete coverage or double traded volume");
        Check(!Validate(Minute(0), Minute(2)).Complete, "The exclusive end minute cannot replace the missing final RTH minute");
        Check(!ProfileCoverageValidation.Validate(source, source with { ConnectionId = "other" }, start, start.AddMinutes(2),
            [Minute(0), Minute(1)], ProfileDataOrigin.FeedNativeLast).Complete, "Other connections cannot supply independent profile data");
        Check(!ProfileCoverageValidation.Validate(source, source with { ContractId = "NQH7" }, start, start.AddMinutes(2),
            [Minute(0), Minute(1)], ProfileDataOrigin.FeedNativeLast).Complete, "Other futures contracts cannot supply independent profile data");
        Check(!ProfileCoverageValidation.Validate(source, source with { TickSize = 0.5 }, start, start.AddMinutes(2),
            [Minute(0), Minute(1)], ProfileDataOrigin.FeedNativeLast).Complete, "Tick-resolution mismatch must fail");
        Check(!ProfileCoverageValidation.Validate(source, source, start, start.AddMinutes(2), [Minute(0), Minute(1)],
            ProfileDataOrigin.Unknown).Complete, "A matching numeric histogram cannot substitute for verified native Last provenance");
        var zeros = Validate(Minute(0, bar: 0, native: 0, bins: Array.Empty<ProfileBin>()),
            Minute(1, bar: 0, native: 0, bins: Array.Empty<ProfileBin>()));
        Check(zeros.Complete && zeros.NativeTotalVolume == 0 && !ProfileGeometry.Calculate(zeros.Bins, 0.25, new(), zeros.Complete).Available,
            "Observed zero traded volume remains distinct from incomplete history and never fabricates a POC");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
