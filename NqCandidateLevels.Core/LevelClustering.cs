namespace NqCandidateLevels.Core;

/// <summary>Exact tick buckets followed by bounded complete-link zones. No averaged or synthetic prices.</summary>
public static class LevelClustering
{
    public static IReadOnlyList<IReadOnlyList<CandidateLevel>> Build(IEnumerable<CandidateLevel> references, double tickSize, int toleranceTicks = 8)
    {
        if (!double.IsFinite(tickSize) || tickSize <= 0 || toleranceTicks is < 0 or > 40)
            throw new ArgumentOutOfRangeException(nameof(toleranceTicks));
        var ordered = references.Where(x => double.IsFinite(x.RawPrice))
            .OrderBy(x => SnapshotBuilder.PriceTick(x.RawPrice, tickSize)).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var zones = new List<IReadOnlyList<CandidateLevel>>();
        var members = new List<CandidateLevel>();
        long minimum = 0;
        foreach (var item in ordered)
        {
            var tick = SnapshotBuilder.PriceTick(item.RawPrice, tickSize);
            if (members.Count > 0 && tick - minimum > toleranceTicks)
            {
                zones.Add(Array.AsReadOnly(members.ToArray()));
                members.Clear();
            }
            if (members.Count == 0) minimum = tick;
            members.Add(item);
        }
        if (members.Count > 0) zones.Add(Array.AsReadOnly(members.ToArray()));
        return Array.AsReadOnly(zones.ToArray());
    }

    public static string ZoneId(IEnumerable<CandidateLevel> members) => "zone:" + StableIdentity.Hash(string.Join("|",
        members.Select(x => x.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(x => $"{x.Length}:{x}")));
}
