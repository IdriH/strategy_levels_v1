namespace NqCandidateLevels.Core;

public sealed record OptionUniverseSelectionResult(IReadOnlyList<OptionSeriesCompatibilityMetadata> SelectedSeries,
    IReadOnlyList<OptionSeriesCompatibilityMetadata> UnqueriedSeries, int SameDayProbeCount, string Status);

/// <summary>Deterministic bounded metadata universe. Date-only same-day entries are probes,
/// never presumed unexpired contracts; their leaf contracts still require compatibility validation.</summary>
public static class OptionUniverseSelection
{
    public static OptionUniverseSelectionResult Select(IEnumerable<OptionSeriesCompatibilityMetadata> series,
        SourceIdentity source, DateTime asOfUtc, int budget = 8)
    {
        if (asOfUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC as-of required.", nameof(asOfUtc));
        if (budget is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(budget));
        var today = DateOnly.FromDateTime(asOfUtc);
        bool Probe(OptionSeriesCompatibilityMetadata s) => s.Expiry!.IsDateOnly && s.Expiry.Date == today;
        var eligible = series.Where(s => !string.IsNullOrWhiteSpace(s.SeriesId) &&
                s.ConnectionId == source.ConnectionId && s.Underlying == OptionUnderlyingEvidence.MatchesSource &&
                OptionContractCompatibility.ValidExpiry(s.Expiry) && s.Expiry!.Utc <= source.ExpirationUtc &&
                (s.Expiry.Utc > asOfUtc || Probe(s)))
            .GroupBy(s => s.SeriesId, StringComparer.Ordinal)
            // Conflicting duplicates do not establish a verified scan request.
            .Where(g => g.Select(s => s.Expiry).Distinct().Count() == 1).Select(g => g.First())
            .OrderBy(s => s.Expiry!.Utc).ThenBy(s => s.SeriesId, StringComparer.Ordinal).ToArray();
        var chosen = new List<OptionSeriesCompatibilityMetadata>();
        void Add(OptionSeriesCompatibilityMetadata? value)
        {
            if (value is not null && chosen.Count < budget && chosen.All(s => s.SeriesId != value.SeriesId)) chosen.Add(value);
        }
        // Prioritize nearest same-day probes within, rather than in addition to, the budget.
        foreach (var value in eligible.Take(Math.Min(4, budget))) Add(value);
        Add(eligible.FirstOrDefault(s => s.Expiry!.Utc > asOfUtc.AddDays(7) && chosen.All(c => c.SeriesId != s.SeriesId)));
        Add(eligible.FirstOrDefault(s => s.Expiry!.Utc > asOfUtc.AddDays(30) && chosen.All(c => c.SeriesId != s.SeriesId)));
        Add(eligible.LastOrDefault(s => chosen.All(c => c.SeriesId != s.SeriesId)));
        foreach (var value in eligible) Add(value);
        var selected = chosen.OrderBy(s => s.Expiry!.Utc).ThenBy(s => s.SeriesId, StringComparer.Ordinal).ToArray();
        var unqueried = eligible.Where(s => selected.All(c => c.SeriesId != s.SeriesId)).ToArray();
        var probes = selected.Count(Probe);
        return new(Array.AsReadOnly(selected), Array.AsReadOnly(unqueried), probes,
            $"{selected.Length}/{eligible.Length} verified series selected; {probes} same-day cutoff probes; " +
            $"{unqueried.Length} unqueried/displaced by scan budget");
    }
}
