namespace NqCandidateLevels.Core;

public sealed record ProfileBin(double Price, double Volume);
public sealed record ProfileSettings(int BinTicks = 1, int SmoothingRadius = 1, int Neighborhood = 1, int HighNodeLimit = 3, int LowNodeLimit = 3);
public enum ProfileLevelKind { Poc, Hvn, Lvn }
public sealed record ProfilePoint(ProfileLevelKind Kind, double Price, double Volume);
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
            for (var j = left; j <= right; j++) smooth[i] += volume[j];
            smooth[i] /= right - left + 1;
        }
        double Price(int i) => (min + i) * settings.BinTicks * tickSize;
        var poc = Array.IndexOf(volume, volume.Max()); // Lowest-price maximum wins ties.
        var points = new List<ProfilePoint> { new(ProfileLevelKind.Poc, Price(poc), volume[poc]) };
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
        points.AddRange(highs.OrderByDescending(x => x.Value).ThenBy(x => x.Index).Take(settings.HighNodeLimit)
            .Select(x => new ProfilePoint(ProfileLevelKind.Hvn, Price(x.Index), volume[x.Index])));
        points.AddRange(lows.OrderBy(x => x.Value).ThenBy(x => x.Index).Take(settings.LowNodeLimit)
            .Select(x => new ProfilePoint(ProfileLevelKind.Lvn, Price(x.Index), volume[x.Index])));
        return new(true, $"Real volume-at-price; {volume.Length} bins × {settings.BinTicks} ticks; smoothing radius {settings.SmoothingRadius}, neighborhood {settings.Neighborhood}", Array.AsReadOnly(points.ToArray()));
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) <= 1e-10 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
}
