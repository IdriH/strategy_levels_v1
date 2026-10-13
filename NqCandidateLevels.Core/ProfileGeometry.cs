namespace NqCandidateLevels.Core;

public sealed record ProfileBin(double Price, double Volume);
public sealed record ProfileSettings(int BinTicks = 1, int SmoothingRadius = 1, int Neighborhood = 1, int HighNodeLimit = 3, int LowNodeLimit = 3);
public enum ProfileLevelKind { Poc, Hvn, Lvn }
public sealed record ProfileHistogram
{
    public IReadOnlyList<ProfileBin> Bins { get; }
    public IReadOnlyList<double> SmoothedVolumes { get; }
    public double MedianPositiveSmoothedVolume { get; }
    public int BinTicks { get; }
    public int SmoothingRadius { get; }
    public int Neighborhood { get; }

    public ProfileHistogram(IReadOnlyList<ProfileBin> bins, IReadOnlyList<double> smoothedVolumes,
        double medianPositiveSmoothedVolume, int binTicks, int smoothingRadius, int neighborhood)
    {
        Bins = Array.AsReadOnly(bins.ToArray()); SmoothedVolumes = Array.AsReadOnly(smoothedVolumes.ToArray());
        MedianPositiveSmoothedVolume = medianPositiveSmoothedVolume;
        BinTicks = binTicks; SmoothingRadius = smoothingRadius; Neighborhood = neighborhood;
    }

    // Constructor owns its nested arrays. All points and successive snapshots can share
    // one immutable histogram without copying up to 100001 bins on each source quote.
    public ProfileHistogram Freeze() => this;
}
public sealed record ProfilePoint(ProfileLevelKind Kind, double Price, double Volume)
{
    public double? SmoothedVolume { get; init; }
    public double? NormalizedProminence { get; init; }
    public double? LeftPeakSmoothedVolume { get; init; }
    public double? RightPeakSmoothedVolume { get; init; }
    public double? LeftPeakPrice { get; init; }
    public double? RightPeakPrice { get; init; }
    public string ProminenceDiagnostic { get; init; } = "Histogram context unavailable";
    public ProfileHistogram? Histogram { get; init; }
    public ProfilePoint Freeze() => this with { Histogram = Histogram?.Freeze() };
}
public sealed record ProfileResult(bool Available, string Status, IReadOnlyList<ProfilePoint> Points);

/// <summary>Geometry over real volume-at-price. Never synthesizes traded volume from OHLCV.</summary>
public static class ProfileGeometry
{
    public static ProfileResult Calculate(IEnumerable<ProfileBin> bins, double tickSize, ProfileSettings settings, bool completeCoverage)
    {
        if (!completeCoverage) return new(false, "Volume profile unavailable: complete independent price-level coverage not established", Array.Empty<ProfilePoint>());
        if (!(tickSize > 0) || !double.IsFinite(tickSize) || settings.BinTicks < 1 || settings.SmoothingRadius < 0 ||
            settings.Neighborhood < 1 || settings.HighNodeLimit < 0 || settings.LowNodeLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(settings));
        var input = bins.ToArray();
        if (input.Any(x => !double.IsFinite(x.Price) || !double.IsFinite(x.Volume) || x.Volume < 0))
            return new(false, "Volume profile unavailable: invalid price-level volume", Array.Empty<ProfilePoint>());
        var grouped = input.Where(x => x.Volume > 0).GroupBy(x => (long)Math.Floor(SnapshotBuilder.PriceTick(x.Price, tickSize) / (double)settings.BinTicks))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Volume));
        if (grouped.Values.Any(x => !double.IsFinite(x)))
            return new(false, "Volume profile unavailable: histogram volume overflow", Array.Empty<ProfilePoint>());
        if (grouped.Count == 0) return new(false, "Volume profile unavailable: genuine zero traded volume", Array.Empty<ProfilePoint>());
        var min = grouped.Keys.Min();
        var max = grouped.Keys.Max();
        if (max - min > 100_000) return new(false, "Volume profile unavailable: requested histogram exceeds 100001 bins", Array.Empty<ProfilePoint>());
        var volume = new double[(int)(max - min + 1)];
        foreach (var pair in grouped) volume[(int)(pair.Key - min)] = pair.Value;
        var smooth = new double[volume.Length];
        for (var i = 0; i < volume.Length; i++)
        {
            var left = Math.Max(0, i - settings.SmoothingRadius);
            var right = Math.Min(volume.Length - 1, i + settings.SmoothingRadius);
            for (var j = left; j <= right; j++) smooth[i] += volume[j] / (right - left + 1);
        }
        double Price(int i) => (min + i) * settings.BinTicks * tickSize;
        var poc = Array.IndexOf(volume, volume.Max()); // Lowest-price maximum wins ties.
        var points = new List<ProfilePoint>();
        var highs = new List<(int Index, double Value)>();
        var lows = new List<(int Index, double Value)>();
        for (var start = 0; start < smooth.Length;)
        {
            var end = start;
            while (end + 1 < smooth.Length && NearlyEqual(smooth[end + 1], smooth[start])) end++;
            if (start >= settings.Neighborhood && end + settings.Neighborhood < smooth.Length)
            {
                var neighbors = Enumerable.Range(start - settings.Neighborhood, settings.Neighborhood)
                    .Concat(Enumerable.Range(end + 1, settings.Neighborhood)).Select(i => smooth[i]).ToArray();
                var center = (start + end) / 2; // Lower midpoint for a flat plateau.
                if (neighbors.All(x => x < smooth[start] && !NearlyEqual(x, smooth[start]))) highs.Add((center, smooth[start]));
                if (neighbors.All(x => x > smooth[start] && !NearlyEqual(x, smooth[start]))) lows.Add((center, smooth[start]));
            }
            start = end + 1;
        }
        var positive = smooth.Where(x => x > 0 && double.IsFinite(x)).Order().ToArray();
        var median = positive.Length % 2 == 1 ? positive[positive.Length / 2] :
            positive[positive.Length / 2 - 1] / 2 + positive[positive.Length / 2] / 2;
        var histogram = new ProfileHistogram(Array.AsReadOnly(volume.Select((v, i) => new ProfileBin(Price(i), v)).ToArray()),
            Array.AsReadOnly(smooth), median, settings.BinTicks, settings.SmoothingRadius, settings.Neighborhood);
        ProfilePoint Point(ProfileLevelKind kind, int index)
        {
            var left = highs.Where(x => x.Index < index).OrderByDescending(x => x.Index).Select(x => ((int Index, double Value)?)x).FirstOrDefault();
            var right = highs.Where(x => x.Index > index).OrderBy(x => x.Index).Select(x => ((int Index, double Value)?)x).FirstOrDefault();
            double? prominence = kind == ProfileLevelKind.Lvn
                ? left.HasValue && right.HasValue && Math.Min(left.Value.Value, right.Value.Value) > 0
                    ? Math.Clamp(1 - smooth[index] / Math.Min(left.Value.Value, right.Value.Value), 0, 1) : null
                : smooth[index] > 0 && median > 0 ? Math.Clamp(Math.Log2(smooth[index] / median), 0, 1) : null;
            return new ProfilePoint(kind, Price(index), volume[index])
            {
                SmoothedVolume = smooth[index], NormalizedProminence = prominence, Histogram = histogram,
                LeftPeakPrice = left.HasValue ? Price(left.Value.Index) : null,
                RightPeakPrice = right.HasValue ? Price(right.Value.Index) : null,
                LeftPeakSmoothedVolume = left?.Value, RightPeakSmoothedVolume = right?.Value,
                ProminenceDiagnostic = prominence.HasValue ? "Validated histogram and actual smoothed geometry" :
                    "No independently identified positive local maxima on both sides; prominence unavailable"
            };
        }
        points.Add(Point(ProfileLevelKind.Poc, poc));
        points.AddRange(highs.OrderByDescending(x => x.Value).ThenBy(x => x.Index).Take(settings.HighNodeLimit)
            .Select(x => Point(ProfileLevelKind.Hvn, x.Index)));
        points.AddRange(lows.OrderBy(x => x.Value).ThenBy(x => x.Index).Take(settings.LowNodeLimit)
            .Select(x => Point(ProfileLevelKind.Lvn, x.Index)));
        return new(true, $"Real volume-at-price; {volume.Length} bins × {settings.BinTicks} ticks; smoothing radius {settings.SmoothingRadius}, neighborhood {settings.Neighborhood}", Array.AsReadOnly(points.ToArray()));
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) <= 1e-10 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
}
