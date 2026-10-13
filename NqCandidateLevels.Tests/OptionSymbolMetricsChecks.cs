using NqCandidateLevels;
using NqCandidateLevels.Core;
using TradingPlatform.BusinessLayer;

internal static class OptionSymbolMetricsChecks
{
    public static void Run()
    {
        ExplicitZeroPreservesFieldEvidence();
        UnknownDefaultsStayUnknown();
        DelayedOpenInterestCompletesCollection().GetAwaiter().GetResult();
        FailedAttachmentCleansOwnedHandlers().GetAwaiter().GetResult();
        CacheReadFailuresCannotEscapeCallbacksOrPreventCleanup();
        OtherConsumersKeepTheirHandlers();
        BandHydrationRestoresBucketCoverage().GetAwaiter().GetResult();
    }

    private static OptionContract Contract() => new("option", "rithmic", "NQZ6", "series",
        new DateTime(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc), 30000, OptionRight.Call);

    private static void ExplicitZeroPreservesFieldEvidence()
    {
        var symbol = new Symbol();
        var subscription = new OptionSymbolMetrics(symbol, true);
        subscription.Subscribe();
        Check(!subscription.HasOpenInterest, "default cached zero cannot complete OI collection");
        symbol.EmitDayBar(0);
        Check(subscription.HasOpenInterest, "vendor-confirmed explicit zero is real OI");
        var supplied = subscription.Apply(Contract()).OpenInterestMetric!;
        Check(supplied.Value == 0 && supplied.Availability == OptionMetricAvailability.SuppliedValid,
            "confirmed DayBar zero is retained as supplied-valid field data");
        symbol.EmitDayBar(double.NaN);
        symbol.EmitQuote();
        symbol.EmitLast();
        subscription.Unsubscribe();
        var result = subscription.Apply(Contract());
        Check(result.OpenInterest == 0 && result.OpenInterestMetric == supplied,
            "volume-only DayBar, quotes, trades and final cache read preserve zero and its receipt time");
        Check(result.OpenInterestPublishedUtc == null && result.OpenInterestMetric!.PublishedUtc == null,
            "market delivery never fabricates an OI publication clock");
        Check(result.TradedVolume == null && result.VolumeMetric?.VolumeSessionConfirmed == false,
            "default volume remains unknown without a confirmed session");
        Check(symbol.HandlerCount == 0, "explicit-zero subscription releases all handlers");
    }

    private static void UnknownDefaultsStayUnknown()
    {
        foreach (var confirmsValidity in new[] { false, true })
        {
            var symbol = new Symbol();
            var subscription = new OptionSymbolMetrics(symbol, confirmsValidity);
            subscription.Subscribe();
            Check(!subscription.HasOpenInterest && !subscription.ReceivedUpdate,
                "cached defaults without delivery remain pending for every vendor");
            subscription.Unsubscribe();
            var result = subscription.Apply(Contract());
            Check(result.OpenInterest == null && result.OpenInterestMetric?.Availability == OptionMetricAvailability.Unknown,
                "unpopulated cached zero is never promoted by cleanup");
        }

        var unknownVendor = new Symbol();
        var unknownSubscription = new OptionSymbolMetrics(unknownVendor);
        unknownSubscription.Subscribe();
        unknownVendor.EmitDayBar(0);
        unknownVendor.EmitQuote();
        unknownVendor.EmitLast();
        unknownSubscription.Unsubscribe();
        Check(unknownSubscription.ReceivedUpdate && !unknownSubscription.HasOpenInterest &&
            unknownSubscription.Apply(Contract()).OpenInterest == null,
            "unverified vendor DayBar zero and unrelated quotes do not confirm field validity");
    }

    private static async Task DelayedOpenInterestCompletesCollection()
    {
        var symbol = new Symbol();
        var subscription = new OptionSymbolMetrics(symbol);
        var collection = OptionMetricHydration.CollectAsync(new[] { subscription }, CancellationToken.None,
            batchWait: TimeSpan.FromSeconds(2));
        symbol.EmitQuote();
        Check(subscription.ReceivedUpdate && !subscription.HasOpenInterest && !collection.IsCompleted,
            "quote delivery with unknown OI keeps the actual adapter collecting");
        symbol.CachedOpenInterest = 123;
        symbol.CachedVolume = 17;
        symbol.EmitLast();
        Check(subscription.HasOpenInterest, "delayed positive cached OI completes after delivery");
        var hydration = await collection;
        var result = subscription.Apply(Contract());
        Check(hydration.Subscribed == 1 && hydration.Pending == 0 && symbol.HandlerCount == 0,
            "delayed OI is collected and adapter handlers are released");
        Check(result.OpenInterest == 123 && result.OpenInterestMetric?.Availability == OptionMetricAvailability.CachedValue &&
            result.OpenInterestMetric.PublishedUtc == null,
            "positive cache supplies OI while retaining unknown publication time");
        Check(result.TradedVolume == 17 && result.VolumeMetric?.VolumeSessionConfirmed == false,
            "positive volume cache still does not establish a trading session");

        symbol = new Symbol { CachedOpenInterest = 12 };
        subscription = new OptionSymbolMetrics(symbol);
        subscription.Subscribe();
        Check(!subscription.HasOpenInterest && subscription.Apply(Contract()).OpenInterest == 12,
            "previous consumer's positive cache does not skip a delivery opportunity");
        subscription.Unsubscribe();
    }

    private static async Task FailedAttachmentCleansOwnedHandlers()
    {
        var symbol = new Symbol();
        var externalDeliveries = 0;
        Action<Symbol, DayBar> external = (_, _) => externalDeliveries++;
        symbol.NewDayBar += external;
        symbol.AfterAdd = name => { if (name == nameof(Symbol.NewQuote)) throw new InvalidOperationException("second add"); };
        var subscription = new OptionSymbolMetrics(symbol);
        var result = await OptionMetricHydration.CollectAsync(new[] { subscription }, CancellationToken.None,
            batchWait: TimeSpan.Zero);
        Check(result.Failed == 1 && result.Subscribed == 0 && result.CleanupFailed == 0,
            "second accessor failure is recorded even if it throws after attaching");
        Check(symbol.DayBarHandlerCount == 1 && symbol.QuoteHandlerCount == 0 && symbol.LastHandlerCount == 0,
            "partial attachment releases exactly the request's DayBar and quote handlers");
        Check(symbol.DayBarRemoveCount == 1 && symbol.QuoteRemoveCount == 1 && symbol.LastRemoveCount == 0,
            "cleanup removes both attempted handlers without detaching an unattempted trade handler");
        symbol.EmitDayBar(10);
        Check(externalDeliveries == 1 && !subscription.ReceivedUpdate,
            "external listener survives partial failure and request handler is inactive");
    }

    private static void CacheReadFailuresCannotEscapeCallbacksOrPreventCleanup()
    {
        var symbol = new Symbol { ReadOpenInterest = () => throw new InvalidOperationException("cache getter") };
        var subscription = new OptionSymbolMetrics(symbol, true);
        subscription.Subscribe();
        Check(symbol.HandlerCount == 3 && subscription.ReadFailures > 0,
            "initial cache getter failure does not prevent activation");
        symbol.EmitQuote();
        symbol.EmitLast();
        symbol.EmitDayBar(0);
        Check(subscription.HasOpenInterest && subscription.ReadFailures == 4,
            "getter exceptions cannot escape SDK callbacks or discard explicit field evidence");
        subscription.Unsubscribe();
        Check(symbol.HandlerCount == 0 && subscription.ReadFailures == 5,
            "final cache getter failure cannot skip owned handler cleanup");
        Check(subscription.Apply(Contract()).OpenInterest == 0,
            "explicit zero survives unreadable cached fields during cleanup");
    }

    private static void OtherConsumersKeepTheirHandlers()
    {
        var symbol = new Symbol();
        var calls = 0;
        Action<Symbol, DayBar> day = (_, _) => calls++;
        Action<Symbol, Quote> quote = (_, _) => calls++;
        Action<Symbol, Last> last = (_, _) => calls++;
        symbol.NewDayBar += day;
        symbol.NewQuote += quote;
        symbol.NewLast += last;
        var subscription = new OptionSymbolMetrics(symbol);
        subscription.Subscribe();
        Check(symbol.HandlerCount == 6, "adapter joins existing consumers");
        subscription.Unsubscribe();
        subscription.Unsubscribe();
        Check(symbol.HandlerCount == 3 && symbol.DayBarRemoveCount == 1 &&
            symbol.QuoteRemoveCount == 1 && symbol.LastRemoveCount == 1,
            "repeated cleanup removes only owned handlers once");
        symbol.EmitDayBar(1);
        symbol.EmitQuote();
        symbol.EmitLast();
        Check(calls == 3 && !subscription.ReceivedUpdate, "existing consumers continue receiving all delivery types");
    }

    private static async Task BandHydrationRestoresBucketCoverage()
    {
        var start = DateTime.UtcNow;
        var source = new SourceIdentity("rithmic", "NQZ6", "NQZ6", start.AddDays(60), .25);
        // Twelve in-band strikes plus one far strike; each stub symbol delivers OI only once attached.
        (OptionContract Contract, Symbol Symbol)[] Chain(HashSet<int> attached, Action<int>? onAttach = null)
        {
            var rows = new List<(OptionContract, Symbol)>();
            foreach (var strike in Enumerable.Range(0, 12).Select(i => 1000d + i * 10).Append(5000))
                foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
                {
                    var symbol = new Symbol();
                    var index = rows.Count;
                    var openInterest = strike == 1080 ? 500 : 50;
                    symbol.AfterAdd = name =>
                    {
                        if (name != nameof(Symbol.NewLast)) return;
                        attached.Add(index);
                        symbol.CachedOpenInterest = openInterest;
                        symbol.EmitLast();
                        onAttach?.Invoke(index);
                    };
                    rows.Add((new OptionContract($"{strike}:{right}", source.ConnectionId, source.ContractId, "series",
                        start.AddDays(3), strike, right, ObservedUtc: start)
                    {
                        OpenInterestMetric = new(null, OptionMetricAvailability.Unknown, start),
                        VolumeMetric = new(null, OptionMetricAvailability.Unknown, start)
                    }, symbol));
                }
            return rows.ToArray();
        }
        OptionConcentrationResult Analyze(IEnumerable<OptionContract> contracts) =>
            OptionConcentration.Analyze(contracts, source, 1055, DateTime.UtcNow);

        var attached = new HashSet<int>();
        var rows = Chain(attached);
        var before = Analyze(rows.Select(r => r.Contract));
        Check(!before.HasUsableMetric && before.Concentrations.All(c => c.OpenInterestCoverage == 0),
            "unsubscribed retrieval reproduces zero percent OI bucket coverage");
        var (hydrated, status) = await OptionBandHydration.HydrateAsync(rows, 1055, 100, 5, TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        var peak = Analyze(hydrated).Concentrations.Single(c => c.Strike == 1080);
        Check(peak.IsEligible && peak.OpenInterestCoverage == 1 && peak.OpenInterestTotal == 1000 &&
            Math.Abs(peak.EffectiveConcentration - .8) < 1e-12,
            "band hydration restores complete OI buckets and a capped measured peak");
        Check(status.Contains("24/24 in-band contracts subscribed") && attached.Count == 24 && !attached.Contains(24) &&
            !attached.Contains(25), "contracts outside the analysis band are never subscribed");
        Check(status.Contains("24/0/0/0 of 24"), "per-expiry diagnostics separate delivered OI from silence: " + status);
        Check(rows.All(r => r.Symbol.HandlerCount == 0), "every hydration batch releases its handlers");

        using var request = new CancellationTokenSource();
        attached = new HashSet<int>();
        rows = Chain(attached, index => { if (index == 9) request.Cancel(); })
            .Select(r => r.Contract.ContractId == "1110:Call"
                ? (r.Contract with { OpenInterest = 77, OpenInterestMetric = new(77, OptionMetricAvailability.CachedValue, start) }, r.Symbol)
                : r).ToArray();
        (hydrated, status) = await OptionBandHydration.HydrateAsync(rows, 1055, 100, 5, TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10), request.Token);
        Check(status.Contains("10/24 in-band") && status.Contains("14 not attempted (deadline)") && attached.Count == 10,
            "a request deadline stops new batches and reports the unattempted remainder");
        Check(hydrated.Single(c => c.ContractId == "1040:Put").OpenInterest == 50 &&
            hydrated.Single(c => c.ContractId == "1110:Call").OpenInterest == 77 &&
            hydrated.Single(c => c.ContractId == "1110:Put").OpenInterestMetric?.Availability == OptionMetricAvailability.Unknown,
            "delivered OI is kept and unattempted contracts keep their retrieval-time reading");
        Check(rows.All(r => r.Symbol.HandlerCount == 0), "deadline cleanup releases the open batch");

        attached = new HashSet<int>();
        rows = Chain(attached);
        (hydrated, status) = await OptionBandHydration.HydrateAsync(rows, 1055, 100, 5, TimeSpan.FromSeconds(1),
            TimeSpan.Zero, CancellationToken.None);
        Check(status.StartsWith("OI hydration skipped") && attached.Count == 0 &&
            hydrated.SequenceEqual(rows.Select(r => r.Contract)), "an exhausted budget opens no subscriptions");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

// Compile the production adapter directly against the smallest SDK event/property
// surface it uses, so its data and cleanup behavior can run without Quantower.
namespace TradingPlatform.BusinessLayer
{
    public enum SymbolVolumeType { Volume, Ticks }
    public sealed class DayBar { public double OpenInterest { get; init; } = double.NaN; }
    public sealed class Quote { }
    public sealed class Last { }

    public sealed class Symbol
    {
        private Action<Symbol, DayBar>? dayBars;
        private Action<Symbol, Quote>? quotes;
        private Action<Symbol, Last>? lasts;
        public double CachedOpenInterest { get; set; }
        public double CachedVolume { get; set; }
        public Func<double>? ReadOpenInterest { get; init; }
        public double OpenInterest => ReadOpenInterest?.Invoke() ?? CachedOpenInterest;
        public double Volume => CachedVolume;
        public SymbolVolumeType VolumeType { get; set; } = SymbolVolumeType.Volume;
        public Action<string>? AfterAdd { get; set; }
        public int DayBarRemoveCount { get; private set; }
        public int QuoteRemoveCount { get; private set; }
        public int LastRemoveCount { get; private set; }
        public int DayBarHandlerCount => dayBars?.GetInvocationList().Length ?? 0;
        public int QuoteHandlerCount => quotes?.GetInvocationList().Length ?? 0;
        public int LastHandlerCount => lasts?.GetInvocationList().Length ?? 0;
        public int HandlerCount => DayBarHandlerCount + QuoteHandlerCount + LastHandlerCount;
        public event Action<Symbol, DayBar> NewDayBar
        {
            add { dayBars += value; AfterAdd?.Invoke(nameof(NewDayBar)); }
            remove { DayBarRemoveCount++; dayBars -= value; }
        }
        public event Action<Symbol, Quote> NewQuote
        {
            add { quotes += value; AfterAdd?.Invoke(nameof(NewQuote)); }
            remove { QuoteRemoveCount++; quotes -= value; }
        }
        public event Action<Symbol, Last> NewLast
        {
            add { lasts += value; AfterAdd?.Invoke(nameof(NewLast)); }
            remove { LastRemoveCount++; lasts -= value; }
        }
        public void EmitDayBar(double oi) => dayBars?.Invoke(this, new DayBar { OpenInterest = oi });
        public void EmitQuote() => quotes?.Invoke(this, new Quote());
        public void EmitLast() => lasts?.Invoke(this, new Last());
    }
}
