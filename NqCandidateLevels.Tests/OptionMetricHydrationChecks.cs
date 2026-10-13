using NqCandidateLevels.Core;

internal static class OptionMetricHydrationChecks
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await BatchesAndPopulatedValues();
        await DelayedOpenInterestAndMissingValues();
        await CancellationKeepsPartialData();
        await FailuresStillCleanEveryHandler();
        await HydrationFeedsCompleteConcentrationSample();
    }

    private static async Task BatchesAndPopulatedValues()
    {
        var active = 0;
        var maximum = 0;
        var order = new List<int>();
        var rows = Enumerable.Range(0, 7).Select(i => new Fake
        {
            OpenInterest = true,
            Attach = () => { maximum = Math.Max(maximum, ++active); order.Add(i); },
            Detach = () => active--
        }).ToArray();
        var result = await OptionMetricHydration.CollectAsync(rows, CancellationToken.None, 2, TimeSpan.Zero);
        Equal(7, result.Attempted, "populated values do not skip selected contracts");
        Equal(7, result.Subscribed, "all selected symbols attached");
        Equal(0, result.Pending, "populated OI completes without timeout");
        Equal(2, maximum, "simultaneous subscriptions stay bounded");
        Equal(0, active, "no attachment remains after collection");
        Check(order.SequenceEqual(Enumerable.Range(0, 7)), "caller order is retained independently of metric values");
        Check(rows.All(x => x.SubscribeCount == 1 && x.UnsubscribeCount == 1), "one attach and detach per selected contract");
    }

    private static async Task DelayedOpenInterestAndMissingValues()
    {
        var row = new Fake();
        var collection = OptionMetricHydration.CollectAsync(new[] { row }, CancellationToken.None,
            batchWait: TimeSpan.FromSeconds(2));
        Check(row.SubscribeCount == 1 && !collection.IsCompleted, "a quote-only subscription cannot finish OI collection");
        // The fake has received a quote but its OI remains absent. Only subsequent OI
        // delivery makes HasOpenInterest true; no quote timestamp is treated as OI.
        row.OpenInterest = true;
        var result = await collection;
        Equal(0, result.Pending, "delayed OI is observed while attached");
        Equal(1, row.UnsubscribeCount, "delayed delivery handler released");

        var missing = new Fake();
        result = await OptionMetricHydration.CollectAsync(new[] { missing }, CancellationToken.None,
            batchWait: TimeSpan.Zero);
        Equal(1, result.Pending, "missing OI remains pending instead of becoming zero");
        Check(!result.WasCanceled && !missing.OpenInterest, "batch deadline fabricates no metric or request cancellation");
        Equal(1, missing.UnsubscribeCount, "timeout releases handler");
    }

    private static async Task CancellationKeepsPartialData()
    {
        using var canceled = new CancellationTokenSource();
        var first = new Fake { OpenInterest = true };
        var second = new Fake { Attach = canceled.Cancel };
        var unvisited = new Fake();
        var result = await OptionMetricHydration.CollectAsync(new[] { first, second, unvisited }, canceled.Token);
        Check(result.WasCanceled, "cancellation returns partial status");
        Equal(2, result.Attempted, "cancellation stops new attachments");
        Equal(2, result.Subscribed, "completed attachments remain counted");
        Equal(1, result.Pending, "partial positive OI survives cancellation");
        Check(first.OpenInterest, "collection leaves metrics available to caller");
        Equal(1, first.UnsubscribeCount, "first handler released after cancellation");
        Equal(1, second.UnsubscribeCount, "cancelling handler released");
        Equal(0, unvisited.SubscribeCount, "no post-cancel attachment");
        result = await OptionMetricHydration.CollectAsync(new[] { unvisited }, canceled.Token);
        Check(result.WasCanceled && result.Attempted == 0, "pre-cancellation opens no handlers");

        using var duringWait = new CancellationTokenSource();
        var waiting = new Fake();
        var collection = OptionMetricHydration.CollectAsync(new[] { waiting }, duringWait.Token);
        duringWait.Cancel();
        result = await collection;
        Check(result.WasCanceled && result.Pending == 1 && waiting.UnsubscribeCount == 1,
            "cancellation during async wait returns and cleans up");
    }

    private static async Task FailuresStillCleanEveryHandler()
    {
        var first = new Fake { OpenInterest = true, Detach = () => throw new InvalidOperationException("detach") };
        var partial = new Fake { Attach = () => throw new InvalidOperationException("partial attachment") };
        var unreadable = new Fake { Read = () => throw new InvalidOperationException("field access") };
        var last = new Fake { OpenInterest = true };
        var result = await OptionMetricHydration.CollectAsync(new[] { first, partial, unreadable, last },
            CancellationToken.None, batchWait: TimeSpan.Zero);
        Equal(4, result.Attempted, "failed symbol cannot stop remaining universe");
        Equal(3, result.Subscribed, "only completed subscriptions count");
        Equal(1, result.Failed, "partial attach failure retained");
        Equal(1, result.CleanupFailed, "detach failure retained");
        Equal(1, result.ReadFailed, "field access failure retained once");
        Equal(1, result.Pending, "unreadable metric stays pending");
        Check(new[] { first, partial, unreadable, last }.All(x => x.UnsubscribeCount == 1),
            "partial attachment, read or cleanup failure cannot skip another handler's cleanup");

        var failedCleanup = new Fake { OpenInterest = true, Detach = () => throw new InvalidOperationException("detach") };
        var sameBatch = new Fake { OpenInterest = true };
        var nextBatch = new Fake { OpenInterest = true };
        result = await OptionMetricHydration.CollectAsync(new[] { failedCleanup, sameBatch, nextBatch },
            CancellationToken.None, 2, TimeSpan.Zero);
        Check(result.CleanupFailed == 1 && result.Attempted == 2 && !result.WasCanceled,
            "cleanup failure stops later batches without masquerading as request cancellation");
        Equal(1, sameBatch.UnsubscribeCount, "remaining current-batch handler is cleaned after earlier failure");
        Equal(0, nextBatch.SubscribeCount, "failed cleanup cannot accumulate subscriptions through later batches");
    }

    private static async Task HydrationFeedsCompleteConcentrationSample()
    {
        var now = new DateTime(2026, 10, 12, 14, 0, 0, DateTimeKind.Utc);
        var source = new SourceIdentity("rithmic", "NQZ6", "NQZ6", SessionCalendar.NqZ6LastTradingUtc, .25);
        var metadata = Enumerable.Range(0, 12).SelectMany(i => new[] { OptionRight.Call, OptionRight.Put }.Select(right =>
            new OptionContract($"{i}:{right}", source.ConnectionId, source.ContractId, "series", now.AddDays(3),
                1000 + i * 10, right, ObservedUtc: now))).ToArray();

        MetricFake[] Chain(Func<OptionContract, double?> value) => metadata.Select(c => new MetricFake
        {
            Delivery = value(c)
        }).ToArray();
        OptionConcentrationResult Analyze(MetricFake[] rows)
        {
            var frozen = metadata.Select((c, i) => c with
            {
                OpenInterest = rows[i].Value,
                OpenInterestMetric = new(rows[i].Value, OptionMetricAvailability.CachedValue, now)
            });
            return OptionConcentration.Analyze(frozen, source, 995, now);
        }
        double Value(OptionContract contract) => contract.Strike == 1080 ? 500 : 50;
        var rows = Chain(c => Value(c));
        var initial = Analyze(rows);
        Check(!initial.HasUsableMetric && initial.Concentrations.All(c => c.OpenInterestCoverage == 0),
            "metadata with unpopulated fields reproduces zero percent bucket coverage");
        await OptionMetricHydration.CollectAsync(rows, CancellationToken.None, 4, TimeSpan.Zero);
        var hydrated = Analyze(rows);
        var peak = hydrated.Concentrations.Single(c => c.Strike == 1080);
        Check(hydrated.HasUsableMetric && peak.IsEligible && peak.OpenInterestCoverage == 1 &&
            peak.OpenInterestTotal == 1000 && peak.EffectiveConcentration == .8,
            "delivered complete call/put OI yields a measured peak with unknown-publication cap");

        var zeros = Chain(_ => 0);
        var zeroResult = await OptionMetricHydration.CollectAsync(zeros, CancellationToken.None, batchWait: TimeSpan.Zero);
        Check(zeroResult.Pending == 24 && !Analyze(zeros).HasUsableMetric,
            "zero after subscription still lacks a field-validity guarantee");
        var missingPuts = Chain(c => c.Right == OptionRight.Call ? Value(c) : null);
        await OptionMetricHydration.CollectAsync(missingPuts, CancellationToken.None, batchWait: TimeSpan.Zero);
        var partialRights = Analyze(missingPuts);
        Check(!partialRights.HasUsableMetric && partialRights.Concentrations.All(c =>
            c.OpenInterestCoverage == 0 && c.OpenInterestTotal == null && c.OpenInterestLowerBound > 0),
            "subscriptions do not turn missing put metrics into complete buckets");

        foreach (var deliveredContracts in new[] { 18, 20 })
        {
            using var deadline = new CancellationTokenSource();
            rows = Chain(c => Value(c));
            rows[deliveredContracts - 1].AfterDelivery = deadline.Cancel;
            var collection = await OptionMetricHydration.CollectAsync(rows, deadline.Token, batchWait: TimeSpan.Zero);
            var partial = Analyze(rows);
            var partialPeak = partial.Concentrations.Single(c => c.Strike == 1080);
            Check(collection.WasCanceled && collection.Subscribed == deliveredContracts,
                "deadline preserves delivered observations and stops new subscriptions");
            Check(partial.ReturnedContractCount == 24 && partialPeak.ReturnedBuckets == 12 &&
                partialPeak.OpenInterestValidBuckets == deliveredContracts / 2 &&
                Math.Abs(partialPeak.OpenInterestCoverage - deliveredContracts / 24d) < 1e-8,
                "deadline retains the full metadata denominator instead of filtering to supplied values");
            Check(partial.HasUsableMetric == (deliveredContracts == 20) && partialPeak.IsEligible == (deliveredContracts == 20),
                "partial hydration must independently meet sample, coverage and neighboring-peak gates");
        }
    }

    private sealed class MetricFake : IOptionMetricSubscription
    {
        public double? Value { get; private set; }
        public double? Delivery { get; init; }
        public Action? AfterDelivery { get; set; }
        public bool HasOpenInterest => Value is > 0;
        public void Subscribe() { Value = Delivery; AfterDelivery?.Invoke(); }
        public void Unsubscribe() { }
    }

    private sealed class Fake : IOptionMetricSubscription
    {
        public volatile bool OpenInterest;
        public Action? Attach { get; init; }
        public Action? Detach { get; init; }
        public Func<bool>? Read { get; init; }
        public int SubscribeCount { get; private set; }
        public int UnsubscribeCount { get; private set; }
        public bool HasOpenInterest => Read?.Invoke() ?? OpenInterest;
        public void Subscribe() { SubscribeCount++; Attach?.Invoke(); }
        public void Unsubscribe() { UnsubscribeCount++; Detach?.Invoke(); }
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual, string message) where T : notnull =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, actual {actual}");
}
