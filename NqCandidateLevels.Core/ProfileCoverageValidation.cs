using System.Globalization;

namespace NqCandidateLevels.Core;

public enum ProfileDataOrigin { Unknown, FeedNativeLast }

/// <summary>One completed independent M1 Last bar and its SDK-native volume analysis.</summary>
public sealed record ProfileMinuteData(
    DateTime StartUtc, double Low, double High, double? BarVolume,
    double? NativeTotalVolume, IReadOnlyList<ProfileBin>? PriceLevels);

public sealed record ProfileCoverageResult(
    bool Complete, string Status, IReadOnlyList<ProfileBin> Bins,
    double NativeTotalVolume, double? BarTotalVolume, int BarVolumeMismatchCount,
    int BarVolumeUnavailableCount = 0);

/// <summary>
/// Validates feed-native volume-at-price against VolumeAnalysisData.Total.Volume.
/// HistoryItemBar.Volume is a separate OHLCV field and remains diagnostic. No bin is
/// scaled, synthesized, filtered, or rounded to make the two streams agree.
/// </summary>
public static class ProfileCoverageValidation
{
    public static ProfileCoverageResult Validate(
        SourceIdentity expectedSource, SourceIdentity actualSource,
        DateTime startUtc, DateTime endUtc, IReadOnlyList<ProfileMinuteData> minutes,
        ProfileDataOrigin origin)
    {
        if (expectedSource is null || actualSource is null ||
            string.IsNullOrWhiteSpace(expectedSource.ConnectionId) || string.IsNullOrWhiteSpace(expectedSource.ContractId) ||
            expectedSource.ConnectionId != actualSource.ConnectionId || expectedSource.ContractId != actualSource.ContractId ||
            !double.IsFinite(expectedSource.TickSize) || expectedSource.TickSize <= 0 ||
            !double.IsFinite(actualSource.TickSize) || Math.Abs(expectedSource.TickSize - actualSource.TickSize) > 1e-9)
            return Unavailable("independent profile source identity mismatch");
        if (origin != ProfileDataOrigin.FeedNativeLast)
            return Unavailable("feed-native Last price-level provenance not established");
        if (startUtc.Kind != DateTimeKind.Utc || endUtc.Kind != DateTimeKind.Utc || endUtc <= startUtc ||
            endUtc - startUtc > TimeSpan.FromHours(8) ||
            startUtc.Ticks % TimeSpan.TicksPerMinute != 0 || endUtc.Ticks % TimeSpan.TicksPerMinute != 0)
            return Unavailable("require a bounded UTC minute interval");
        var expectedMinutes = checked((int)((endUtc.Ticks - startUtc.Ticks) / TimeSpan.TicksPerMinute));
        if (minutes is null || minutes.Count != expectedMinutes)
            return Unavailable($"incomplete RTH coverage ({minutes?.Count ?? 0}/{expectedMinutes} minute bars)");

        var tickSize = expectedSource.TickSize;
        var totalsByPrice = new Dictionary<double, double>();
        var nativeSessionVolume = 0d;
        var barSessionVolume = 0d;
        var barMismatchCount = 0;
        var barUnavailableCount = 0;
        // Adapter resolves same-timestamp corrections before this immutable validation.
        // Duplicates, out-of-window bars, and gaps can never add volume here.
        var ordered = minutes.OrderBy(m => m?.StartUtc).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var minute = ordered[index];
            if (minute is null || minute.StartUtc.Kind != DateTimeKind.Utc ||
                minute.StartUtc != startUtc.AddMinutes(index))
                return Unavailable("incomplete or duplicate RTH minute boundaries");
            if (!double.IsFinite(minute.Low) || !double.IsFinite(minute.High) ||
                minute.Low <= 0 || minute.High < minute.Low ||
                !OnTickGrid(minute.Low, tickSize) || !OnTickGrid(minute.High, tickSize))
                return Unavailable($"invalid Last price bounds at {minute.StartUtc:HH:mm} UTC");
            if (!ValidVolume(minute.NativeTotalVolume))
                return Unavailable($"SDK-native total volume missing at {minute.StartUtc:HH:mm} UTC");
            var nativeTotal = minute.NativeTotalVolume!.Value;
            var validBarVolume = ValidVolume(minute.BarVolume);
            if (nativeTotal == 0 && validBarVolume && minute.BarVolume!.Value > 0)
                return Unavailable($"native zero volume conflicts with positive bar volume at {minute.StartUtc:HH:mm} UTC");
            var levels = minute.PriceLevels;
            // Null is missing data. An explicit empty collection with native total zero
            // represents a complete, observed minute with no trades.
            if (levels is null || levels.Count > 20_000 || nativeTotal > 0 && levels.Count == 0)
                return Unavailable($"missing/bounded native price-level coverage at {minute.StartUtc:HH:mm} UTC");
            var levelTotal = 0d;
            foreach (var level in levels)
            {
                if (level is null || !double.IsFinite(level.Price) || level.Price <= 0 ||
                    !double.IsFinite(level.Volume) || level.Volume < 0 || !OnTickGrid(level.Price, tickSize) ||
                    level.Price < minute.Low - tickSize * 0.00001 || level.Price > minute.High + tickSize * 0.00001)
                    return Unavailable($"invalid/out-of-range native price level at {minute.StartUtc:HH:mm} UTC");
                levelTotal += level.Volume;
                if (!double.IsFinite(levelTotal)) return Unavailable("native price-level volume overflow");
            }
            if (!SameVolume(levelTotal, nativeTotal))
                return Unavailable($"incomplete native price-level traded volume at {minute.StartUtc:HH:mm} UTC (levels {Number(levelTotal)}, native total {Number(nativeTotal)})");
            nativeSessionVolume += nativeTotal;
            if (!double.IsFinite(nativeSessionVolume)) return Unavailable("native total volume overflow");
            if (validBarVolume)
            {
                barSessionVolume += minute.BarVolume!.Value;
                if (!double.IsFinite(barSessionVolume)) return Unavailable("bar volume overflow");
                if (!SameVolume(minute.BarVolume.Value, nativeTotal)) barMismatchCount++;
            }
            else barUnavailableCount++;
            foreach (var level in levels)
            {
                totalsByPrice.TryGetValue(level.Price, out var previous);
                var combined = previous + level.Volume;
                if (!double.IsFinite(combined)) return Unavailable("native histogram volume overflow");
                totalsByPrice[level.Price] = combined;
            }
            if (totalsByPrice.Count > 100_001)
                return Unavailable("histogram exceeds 100001 price levels");
        }
        // Preserve exact supplied prices, zero bins, and total native volume. Geometry
        // may subsequently group/smooth these bins according to explicit user settings.
        var bins = Array.AsReadOnly(totalsByPrice.OrderBy(l => l.Key).Select(l => new ProfileBin(l.Key, l.Value)).ToArray());
        var status = $"native price-level coverage verified: {expectedMinutes}/{expectedMinutes} RTH minutes; volume {Number(nativeSessionVolume)}";
        if (barMismatchCount > 0)
            status += $"; OHLCV/native volume differs in {barMismatchCount} minutes (OHLCV {(barUnavailableCount == 0 ? Number(barSessionVolume) : "partly unavailable")}); native volumes preserved";
        if (barUnavailableCount > 0) status += $"; OHLCV volume unavailable in {barUnavailableCount} minutes";
        return new(true, status, bins, nativeSessionVolume,
            barUnavailableCount == 0 ? barSessionVolume : null, barMismatchCount, barUnavailableCount);
    }

    private static bool ValidVolume(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0;
    private static bool OnTickGrid(double price, double tickSize)
    {
        var ticks = price / tickSize;
        return double.IsFinite(ticks) && ticks >= long.MinValue && ticks < long.MaxValue &&
            Math.Abs(ticks - Math.Round(ticks)) < 0.00001;
    }
    private static bool SameVolume(double first, double second) =>
        Math.Abs(first - second) <= Math.Max(0.00001, Math.Max(first, second) * 0.000001);
    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static ProfileCoverageResult Unavailable(string reason) =>
        new(false, "volume profile unavailable: " + reason, Array.Empty<ProfileBin>(), 0, null, 0);
}
