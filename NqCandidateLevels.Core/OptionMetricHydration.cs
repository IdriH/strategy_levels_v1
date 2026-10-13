using System.Diagnostics;

namespace NqCandidateLevels.Core;

/// <summary>A request-owned attachment to an option symbol. A populated positive OI value
/// can finish collection; receipt of an unrelated quote cannot.</summary>
public interface IOptionMetricSubscription
{
    void Subscribe();
    bool HasOpenInterest { get; }
    void Unsubscribe();
}

public sealed record OptionMetricHydrationResult(int Attempted, int Subscribed, int Failed, int Pending, bool WasCanceled)
{
    public int ReadFailed { get; init; }
    public int CleanupFailed { get; init; }
}

/// <summary>Collects a caller-selected, value-independent universe in bounded batches.
/// It owns only the handlers attached by these wrappers, never the underlying symbols.</summary>
public static class OptionMetricHydration
{
    private sealed class Entry(IOptionMetricSubscription subscription)
    {
        public IOptionMetricSubscription Subscription { get; } = subscription;
        public bool Subscribed { get; set; }
        public bool HasOpenInterest { get; set; }
        public bool ReadFailed { get; set; }
    }

    /// <summary>Cancellation releases the current batch and returns partial counters.
    /// Callers distinguish their request deadline from source-lifetime cancellation.
    /// Failed counts attachment failures; pending counts attached wrappers without observed
    /// OI when released. These counters do not establish metric validity or publication time.</summary>
    public static async Task<OptionMetricHydrationResult> CollectAsync(
        IReadOnlyList<IOptionMetricSubscription> subscriptions, CancellationToken cancellationToken,
        int batchSize = 128, TimeSpan? batchWait = null)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var wait = batchWait ?? TimeSpan.FromSeconds(3);
        if (wait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(batchWait));
        var selected = subscriptions.ToArray();
        if (selected.Any(x => x is null)) throw new ArgumentException("Null option subscription.", nameof(subscriptions));
        var attempted = 0;
        var subscribed = 0;
        var failed = 0;
        var pending = 0;
        var readFailed = 0;
        var cleanupFailed = 0;
        var canceled = false;

        void Observe(Entry entry)
        {
            if (!entry.Subscribed || entry.HasOpenInterest || entry.ReadFailed) return;
            try { entry.HasOpenInterest = entry.Subscription.HasOpenInterest; }
            catch { entry.ReadFailed = true; readFailed++; }
        }

        for (var offset = 0; offset < selected.Length;)
        {
            if (cancellationToken.IsCancellationRequested) { canceled = true; break; }
            var batch = new List<Entry>(Math.Min(batchSize, selected.Length - offset));
            try
            {
                var end = offset + Math.Min(batchSize, selected.Length - offset);
                for (; offset < end; offset++)
                {
                    if (cancellationToken.IsCancellationRequested) { canceled = true; break; }
                    // Register cleanup before attaching: Subscribe may attach one handler
                    // and throw before attaching another.
                    var entry = new Entry(selected[offset]);
                    batch.Add(entry);
                    attempted++;
                    try
                    {
                        entry.Subscription.Subscribe();
                        entry.Subscribed = true;
                        subscribed++;
                    }
                    catch { failed++; }
                }

                var elapsed = Stopwatch.StartNew();
                while (!canceled)
                {
                    if (cancellationToken.IsCancellationRequested) { canceled = true; break; }
                    foreach (var entry in batch) Observe(entry);
                    if (batch.All(x => !x.Subscribed || x.HasOpenInterest || x.ReadFailed)) break;
                    var remaining = wait - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) break;
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
            }
            finally
            {
                foreach (var entry in batch)
                {
                    Observe(entry);
                    if (entry.Subscribed && !entry.HasOpenInterest) pending++;
                    try { entry.Subscription.Unsubscribe(); }
                    catch { cleanupFailed++; }
                }
            }
            // An unsuccessful detach may have left a listener alive. Do not open more
            // batches and silently exceed the request's simultaneous-subscription bound.
            if (canceled || cleanupFailed > 0) break;
        }
        return new(attempted, subscribed, failed, pending, canceled || cancellationToken.IsCancellationRequested)
        {
            ReadFailed = readFailed, CleanupFailed = cleanupFailed
        };
    }
}
