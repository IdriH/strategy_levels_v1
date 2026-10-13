using NqCandidateLevels.Core;
using TradingPlatform.BusinessLayer;

namespace NqCandidateLevels;

/// <summary>
/// Request-owned market-data attachment to one option symbol. A symbol returned by a strike
/// request carries default fields until the feed delivers data for it; attaching the day-bar,
/// quote and trade handlers requests that delivery. Only handlers attached here are removed,
/// each at most once, so other consumers of the same platform symbol keep theirs.
///
/// Field evidence stays conservative. A positive OI (cached or day-bar) is a cached value with
/// unknown publication time. Zero stays unknown unless the vendor is known to confirm day-bar
/// field validity, in which case an explicit day-bar OI, including zero, is supplied-valid.
/// No delivery establishes an OI publication clock or a confirmed volume session.
/// </summary>
public sealed class OptionSymbolMetrics : IOptionMetricSubscription
{
    // SDK fields are read outside the gate and recorded under it, so a feed thread raising an
    // event while holding a platform lock can never wait on a reader that is waiting on the SDK.
    private readonly record struct FieldRead(DateTime Utc, double? DayBarOpenInterest, double? OpenInterest,
        double? Volume, int Failures);

    private readonly object gate = new();
    private readonly Symbol symbol;
    private readonly bool confirmsValidity;
    private bool started, activated, active;
    private bool dayBarAttached, quoteAttached, lastAttached;
    private bool receivedUpdate, deliveredOpenInterest, reportedZeroOpenInterest;
    private (double Value, DateTime ObservedUtc)? explicitOpenInterest, cachedOpenInterest, cachedVolume;
    private DateTime? lastReadUtc;
    private int readFailures;

    public OptionSymbolMetrics(Symbol symbol, bool confirmsValidity = false)
    {
        this.symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        this.confirmsValidity = confirmsValidity;
    }

    /// <summary>All handlers attached and the subscription-time cache read was taken.</summary>
    public bool Activated { get { lock (gate) return activated; } }
    /// <summary>Any day-bar, quote or trade delivery arrived while attached.</summary>
    public bool ReceivedUpdate { get { lock (gate) return receivedUpdate; } }
    /// <summary>OI evidence observed after subscribing. A positive cache left by another consumer
    /// does not count until a delivery has had the opportunity to refresh it.</summary>
    public bool HasOpenInterest { get { lock (gate) return explicitOpenInterest.HasValue || deliveredOpenInterest; } }
    public int ReadFailures { get { lock (gate) return readFailures; } }
    /// <summary>Diagnostic only: a day bar explicitly carried zero OI while attached.</summary>
    public bool ReportedZeroOpenInterest { get { lock (gate) return reportedZeroOpenInterest; } }

    public void Subscribe()
    {
        lock (gate)
        {
            if (started) throw new InvalidOperationException("Option metric subscriptions are single-use.");
            started = true;
            active = true;
        }
        try
        {
            // Record each attempt before attaching: an event accessor may attach and then throw.
            lock (gate) dayBarAttached = true;
            symbol.NewDayBar += OnDayBar;
            lock (gate) quoteAttached = true;
            symbol.NewQuote += OnQuote;
            lock (gate) lastAttached = true;
            symbol.NewLast += OnLast;
        }
        catch
        {
            try { Release(finalRead: false); }
            catch { /* The attachment failure is the reported error. */ }
            throw;
        }
        var initial = ReadFields(null);
        lock (gate)
        {
            if (!active) return;
            Record(initial, delivery: false);
            activated = true;
        }
    }

    public void Unsubscribe() => Release(finalRead: true);

    /// <summary>Applies the observed field evidence to retrieval metadata. Retrieval identity and
    /// observation time are retained; no OI publication time is supplied.</summary>
    public OptionContract Apply(OptionContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        lock (gate)
        {
            var unknownAt = lastReadUtc ?? contract.ObservedUtc ?? DateTime.UtcNow;
            OptionMetricObservation openInterest = explicitOpenInterest is { } supplied
                ? new(supplied.Value, OptionMetricAvailability.SuppliedValid, supplied.ObservedUtc)
                : cachedOpenInterest is { } cached
                    ? new(cached.Value, OptionMetricAvailability.CachedValue, cached.ObservedUtc)
                    : new(null, OptionMetricAvailability.Unknown, unknownAt);
            OptionMetricObservation volume = cachedVolume is { } traded
                ? new(traded.Value, OptionMetricAvailability.CachedValue, traded.ObservedUtc)
                : new(null, OptionMetricAvailability.Unknown, unknownAt);
            return contract with
            {
                OpenInterest = openInterest.Value,
                TradedVolume = volume.Value,
                OpenInterestPublishedUtc = null,
                OpenInterestMetric = openInterest,
                VolumeMetric = volume
            };
        }
    }

    private void Release(bool finalRead)
    {
        bool read;
        lock (gate) read = active && finalRead;
        var final = read ? ReadFields(null) : default;
        bool dayBar, quote, last;
        lock (gate)
        {
            if (read && active) Record(final, delivery: false);
            active = false;
            dayBar = dayBarAttached; quote = quoteAttached; last = lastAttached;
            // Each owned handler is detached at most once, even when cleanup is repeated.
            dayBarAttached = quoteAttached = lastAttached = false;
        }
        Exception? failure = null;
        if (dayBar) try { symbol.NewDayBar -= OnDayBar; } catch (Exception ex) { failure ??= ex; }
        if (quote) try { symbol.NewQuote -= OnQuote; } catch (Exception ex) { failure ??= ex; }
        if (last) try { symbol.NewLast -= OnLast; } catch (Exception ex) { failure ??= ex; }
        if (failure != null) throw new InvalidOperationException("Option metric handler cleanup failed.", failure);
    }

    private void OnDayBar(Symbol sender, DayBar bar) => Deliver(bar);
    private void OnQuote(Symbol sender, Quote quote) => Deliver(null);
    private void OnLast(Symbol sender, Last last) => Deliver(null);

    private void Deliver(DayBar? bar)
    {
        try
        {
            lock (gate) if (!active) return;
            var read = ReadFields(bar);
            lock (gate)
            {
                if (!active) return;
                Record(read, delivery: true);
            }
        }
        catch { /* SDK callbacks must never observe adapter failures. */ }
    }

    private FieldRead ReadFields(DayBar? bar)
    {
        var failures = 0;
        double? dayBar = null, openInterest = null, volume = null;
        if (bar != null)
            try { dayBar = bar.OpenInterest; } catch { failures++; }
        try { openInterest = symbol.OpenInterest; } catch { failures++; }
        try { if (symbol.VolumeType == SymbolVolumeType.Volume) volume = symbol.Volume; } catch { failures++; }
        return new(DateTime.UtcNow, dayBar, openInterest, volume, failures);
    }

    // Caller holds the gate. Default zero is never promoted to a value, and an older read that
    // is recorded late never replaces a newer observation.
    private void Record(FieldRead read, bool delivery)
    {
        readFailures += read.Failures;
        if (lastReadUtc is null || read.Utc > lastReadUtc) lastReadUtc = read.Utc;
        if (delivery) receivedUpdate = true;
        // NaN means this day bar carried no OI field (for example a volume-only update).
        if (read.DayBarOpenInterest is double dayBar && double.IsFinite(dayBar) && dayBar >= 0)
        {
            if (dayBar == 0) reportedZeroOpenInterest = true;
            if (confirmsValidity)
            {
                if (explicitOpenInterest is null || read.Utc >= explicitOpenInterest.Value.ObservedUtc)
                    explicitOpenInterest = (dayBar, read.Utc);
            }
            else if (dayBar > 0)
            {
                Cache(ref cachedOpenInterest, dayBar, read.Utc);
                deliveredOpenInterest = true;
            }
        }
        if (read.OpenInterest is double openInterest && double.IsFinite(openInterest) && openInterest > 0)
        {
            Cache(ref cachedOpenInterest, openInterest, read.Utc);
            if (receivedUpdate) deliveredOpenInterest = true;
        }
        if (read.Volume is double volume && double.IsFinite(volume) && volume > 0)
            Cache(ref cachedVolume, volume, read.Utc);
    }

    private static void Cache(ref (double Value, DateTime ObservedUtc)? field, double value, DateTime observedUtc)
    {
        if (field is null || observedUtc >= field.Value.ObservedUtc) field = (value, observedUtc);
    }
}

/// <summary>
/// Collects OI for the contracts of one retrieval's analysis band. The band is fixed to the
/// retrieval center and half-width, so contracts outside it never enter a concentration sample
/// and are never subscribed. Batches are bounded and each is released before the next opens.
/// </summary>
public static class OptionBandHydration
{
    public static async Task<(OptionContract[] Contracts, string Status)> HydrateAsync(
        IReadOnlyList<(OptionContract Contract, Symbol Symbol)> rows, double center, double halfWidth,
        int batchSize, TimeSpan batchWait, TimeSpan budget, CancellationToken requestToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var contracts = rows.Select(r => r.Contract).ToArray();
        // Nearest expiries go first: if the deadline interrupts the scan, the expiries already
        // collected keep their full coverage instead of every expiry being partially sampled.
        var selected = rows.Where(r => r.Symbol != null && Math.Abs(r.Contract.Strike - center) <= halfWidth)
            .GroupBy(r => r.Contract.ContractId, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(r => r.Contract.ExpiryUtc).ThenBy(r => r.Contract.Strike).ThenBy(r => r.Contract.Right)
            .ThenBy(r => r.Contract.ContractId, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) return (contracts, "OI hydration: no in-band contracts");
        if (budget <= TimeSpan.Zero)
            return (contracts, $"OI hydration skipped: request deadline reached; {selected.Length} in-band contracts cached-only");
        batchSize = Math.Clamp(batchSize, 1, 512);
        batchWait = batchWait < TimeSpan.Zero ? TimeSpan.Zero : batchWait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : batchWait;
        // The vendor is not known to confirm DayBar field validity, so a delivered zero stays unknown.
        var metrics = selected.ToDictionary(r => r.Contract.ContractId, r => new OptionSymbolMetrics(r.Symbol),
            StringComparer.Ordinal);
        OptionMetricHydrationResult result;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestToken))
        {
            deadline.CancelAfter(budget);
            // A deadline returns partial counters; observations already delivered remain usable.
            result = await OptionMetricHydration.CollectAsync(
                selected.Select(r => (IOptionMetricSubscription)metrics[r.Contract.ContractId]).ToArray(),
                deadline.Token, batchSize, batchWait).ConfigureAwait(false);
        }
        // Contracts whose batch never opened keep their retrieval-time cached reading.
        var hydrated = contracts.Select(c => metrics.TryGetValue(c.ContractId, out var m) && m.Activated ? m.Apply(c) : c).ToArray();
        var delivered = metrics.Values.Count(m => m.HasOpenInterest);
        var status = $"OI hydration: {result.Subscribed}/{selected.Length} in-band contracts subscribed in batches of {batchSize}; " +
            $"{delivered} delivered OI, {result.Pending} without OI (zero/unknown)";
        if (result.Failed != 0) status += $", {result.Failed} attach failures";
        if (result.ReadFailed != 0) status += $", {result.ReadFailed} read failures";
        if (result.Attempted < selected.Length)
            status += $"; {selected.Length - result.Attempted} not attempted" +
                (result.CleanupFailed != 0 ? " (cleanup stop)" : result.WasCanceled ? " (deadline)" : "");
        if (result.CleanupFailed != 0) status += $"; {result.CleanupFailed} handler cleanups failed, later batches stopped";
        // Separates feed silence (latency/entitlement) from explicit zero OI per expiry.
        var byExpiry = selected.GroupBy(r => r.Contract.ExpiryUtc).OrderBy(g => g.Key).Select(g =>
        {
            var m = g.Select(r => metrics[r.Contract.ContractId]).Where(x => x.Activated).ToArray();
            var oi = m.Count(x => x.HasOpenInterest);
            var zero = m.Count(x => !x.HasOpenInterest && x.ReportedZeroOpenInterest);
            var updateOnly = m.Count(x => !x.HasOpenInterest && !x.ReportedZeroOpenInterest && x.ReceivedUpdate);
            var silent = m.Count(x => !x.ReceivedUpdate);
            return $"{g.Key:MM-dd HH:mm} {oi}/{zero}/{updateOnly}/{silent} of {g.Count()}";
        });
        status += "; by expiry UTC (OI/zero OI/data without OI/no data): " + string.Join(", ", byExpiry);
        return (hydrated, status);
    }
}
