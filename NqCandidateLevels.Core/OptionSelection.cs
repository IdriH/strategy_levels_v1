namespace NqCandidateLevels.Core;

public enum OptionRight { Call, Put, Unknown }
public sealed record OptionContract(string ContractId, string ConnectionId, string UnderlyingContractId, string SeriesId,
    DateTime ExpiryUtc, double Strike, OptionRight Right, double? OpenInterest = null, double? TradedVolume = null,
    DateTime? ObservedUtc = null, DateTime? OpenInterestPublishedUtc = null);
public sealed record OptionStrike(double Strike, IReadOnlyList<OptionContract> Contracts);
public sealed record OptionSelectionResult(string Status, int IncludedSeriesCount, IReadOnlyList<OptionStrike> Strikes);

public static class OptionSelection
{
    public static OptionSelectionResult Select(IEnumerable<OptionContract> options, SourceIdentity source, double sourcePrice,
        DateTime asOfUtc, int seriesLimit = 2, int strikesEachSide = 5)
    {
        if (seriesLimit < 1 || strikesEachSide < 1) throw new ArgumentOutOfRangeException(nameof(seriesLimit));
        if (!double.IsFinite(sourcePrice)) return new("Options unavailable: valid NQZ6 source price required", 0, Array.Empty<OptionStrike>());
        var verified = options.Where(x => x.ConnectionId == source.ConnectionId && x.UnderlyingContractId == source.ContractId &&
            !string.IsNullOrWhiteSpace(x.ContractId) && !string.IsNullOrWhiteSpace(x.SeriesId) &&
            x.ExpiryUtc.Kind == DateTimeKind.Utc && x.ExpiryUtc > asOfUtc && double.IsFinite(x.Strike) &&
            (!x.ObservedUtc.HasValue || x.ObservedUtc.Value <= asOfUtc)).ToArray();
        var series = verified.GroupBy(x => x.SeriesId).OrderBy(g => g.Min(x => x.ExpiryUtc)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(seriesLimit).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var eligible = verified.Where(x => series.Contains(x.SeriesId)).Select(x => x with
        {
            OpenInterest = x.OpenInterest.HasValue && double.IsFinite(x.OpenInterest.Value) && x.OpenInterest >= 0 &&
                (!x.OpenInterestPublishedUtc.HasValue || x.OpenInterestPublishedUtc <= asOfUtc) ? x.OpenInterest : null,
            TradedVolume = x.TradedVolume.HasValue && double.IsFinite(x.TradedVolume.Value) && x.TradedVolume >= 0 ? x.TradedVolume : null
        }).ToArray();
        var distinct = eligible.Select(x => x.Strike).Distinct().ToArray();
        var strikes = distinct.Where(x => x < sourcePrice).OrderDescending().Take(strikesEachSide)
            .Concat(distinct.Where(x => x > sourcePrice).Order().Take(strikesEachSide))
            .Concat(distinct.Where(x => x == sourcePrice)).ToHashSet();
        var grouped = eligible.Where(x => strikes.Contains(x.Strike)).GroupBy(x => x.Strike).OrderBy(g => g.Key)
            .Select(g => new OptionStrike(g.Key, Array.AsReadOnly(g.DistinctBy(x => x.ContractId)
                .OrderBy(x => x.ExpiryUtc).ThenBy(x => x.ContractId, StringComparer.Ordinal).ToArray()))).ToArray();
        return new OptionSelectionResult(grouped.Length == 0 ? "Options unavailable: no unexpired, verified NQZ6 underlier contracts" :
            $"Verified NQZ6 underlying; {series.Count} series, {grouped.Length} distinct strikes; missing OI/volume remain unknown",
            series.Count, Array.AsReadOnly(grouped));
    }
}
