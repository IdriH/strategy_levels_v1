using System.Collections.Concurrent;
using NqCandidateLevels.Core;
using TradingPlatform.BusinessLayer;

namespace NqCandidateLevels;

internal sealed record AdapterSettings(string ConnectionId, string ContractId, string CalendarPath,
    EngineSettings Engine, ManualBias Bias, bool Options, int OptionSeries, int StrikesEachSide,
    int OptionRefreshSeconds, bool Profile, int ProfileBinTicks, int ProfileNeighborhood,
    int ProfileDisplayLimit, int StaleSeconds)
{
    public ScoringSettings Scoring { get; init; } = new();
    public OptionConcentrationSettings OptionConcentration { get; init; } = new();
}

internal sealed record AdapterView(LevelSnapshot? Snapshot, string Health, string Options, string Profile,
    string Connection, string Coverage, string Error = "")
{
    public ScoredLevelSnapshot? ScoredSnapshot { get; init; }
}

/// <summary>Each instance owns one independent Last/M1 history, its handlers, and cancelable workers.</summary>
internal sealed class SourceDataAdapter : IDisposable
{
    private readonly AdapterSettings settings;
    private readonly Action<AdapterView> publish;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? sourceLifetime;
    private readonly SemaphoreSlim worker = new(1, 1);
    private readonly ConcurrentDictionary<DateTime, SourceBar> pending = new();
    private readonly System.Threading.Timer timer;
    private ResolvedSource? source;
    private HistoricalData? history;
    private LevelEngine? engine;
    private SessionCalendar? calendar;
    private volatile bool disposed;
    private volatile bool reinitializeRequested;
    private bool initialized;
    private int starting;
    private int optionBusy;
    private int profileBusy;
    private DateTime nextRetryUtc;
    private DateTime nextOptionsUtc;
    private DateTime nextProfileUtc;
    private long epoch;
    private volatile bool optionalDirty;
    private long profileRevision;
    private bool profileWasCorrected;
    private volatile ManualBias bias;
    private DateTime? profileSession;
    private DateTime? profileSessionEnd;
    private long tickVersion;
    private long seenTickVersion;
    private readonly object quoteGate = new();
    private double? latestPrice;
    private DateTime observedUtc;
    private OptionsProviderSnapshot? optionsResult;
    private ProfileProviderSnapshot? profileResult;
    private string optionsStatus = "options awaiting verified NQZ6 source";
    private string profileStatus = "volume profile disabled";
    private string lastHealth = "";
    private OptionConcentrationResult? selectedOptions;
    private OptionConcentrationResult? optionsAnalysis;
    private volatile ScoringSettings scoringSettings;
    private volatile OptionConcentrationSettings concentrationSettings;
    private OptionConcentrationSettings? analyzedSettings;
    private DateOnly analyzedDate;
    private DateTime analysisExpiryUtc;
    private LevelSnapshot? lastRawSnapshot;
    private OptionsProviderSnapshot? selectedOptionsFrom;
    private double? selectedOptionsPrice;
    private string coverage = "loading independent NQZ6 Last/M1 history";
    private int barCount;
    private DateTime? firstBarUtc;
    private DateTime? lastBarUtc;

    public SourceDataAdapter(AdapterSettings settings, Action<AdapterView> publish)
    {
        this.settings = settings;
        bias = settings.Bias;
        scoringSettings = settings.Scoring;
        concentrationSettings = settings.OptionConcentration;
        this.publish = publish;
        timer = new System.Threading.Timer(_ => _ = PumpAsync(), null, 0, 500);
    }

    private async Task PumpAsync()
    {
        if (disposed || !await worker.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (disposed) return;
            if (reinitializeRequested)
            { reinitializeRequested = false; initialized = false; nextRetryUtc = DateTime.UtcNow; }
            if (!initialized)
            {
                if (DateTime.UtcNow < nextRetryUtc || Interlocked.Exchange(ref starting, 1) != 0) return;
                try { await InitializeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (!disposed)
                {
                    CleanupSource();
                    publish(new(null, "source unavailable", optionsStatus, profileStatus, settings.ConnectionId, coverage, ex.Message));
                    nextRetryUtc = DateTime.UtcNow.AddSeconds(15);
                }
                finally { starting = 0; }
                if (!initialized) return;
            }
            if (disposed || source == null || engine == null || calendar == null) return;
            var now = DateTime.UtcNow;
            var changed = new List<SourceBar>();
            foreach (var key in pending.Keys) if (pending.TryRemove(key, out var bar)) changed.Add(bar);
            double? price; DateTime asOf; long version;
            lock (quoteGate) { price = latestPrice; asOf = observedUtc; version = tickVersion; }
            if (asOf == default) asOf = lastBarUtc ?? now;
            if (changed.Count > 0)
            {
                var cachedProfile = Volatile.Read(ref profileResult);
                if (profileSession.HasValue && changed.Any(b => b.StartUtc >= profileSession.Value &&
                    b.EndUtc <= (cachedProfile?.EndUtc ?? profileSessionEnd)))
                {
                    Interlocked.Increment(ref profileRevision);
                    profileWasCorrected = true;
                    Volatile.Write(ref profileResult, null);
                    profileStatus = "volume profile invalidated by source-history update; refresh pending";
                }
                engine.UpdateBars(changed, asOf, now);
                lastBarUtc = changed.Select(b => b.StartUtc).Append(lastBarUtc ?? DateTime.MinValue).Max();
            }
            if (price.HasValue) engine.SetSourcePrice(price.Value, asOf);
            var wallSession = calendar.GetSession(calendar.GetTradingDate(now));
            var marketOpen = wallSession.IsCertain && wallSession.FuturesIntervals.Any(i => i.Contains(now));
            var connected = source.Connection.State == ConnectionState.Connected;
            var expired = now >= SessionCalendar.NqZ6LastTradingUtc;
            var stale = observedUtc == default || now - asOf > TimeSpan.FromSeconds(settings.StaleSeconds);
            var active = connected && !expired && marketOpen && !stale;
            var health = expired ? "expired — inactive context; no rollover" : !connected ? "disconnected — inactive context" :
                !wallSession.IsCertain ? "schedule uncertain — inactive context" : !marketOpen ? "market closed — inactive context" :
                stale ? "open market / stale NQZ6 feed — inactive context" : "live independent NQZ6 source";
            if (!connected)
            {
                sourceLifetime?.Cancel();
                initialized = false;
                nextRetryUtc = now.AddSeconds(5);
            }
            StartOptionalRequests(asOf, now);
            SelectCachedOptions(asOf, now);
            var statuses = new Dictionary<string, string> { ["Options"] = optionsStatus, ["VolumeProfile"] = profileStatus };
            // Engine providers are cached by data revision. Quote geometry is refreshed without rescanning history.
            var snapshot = engine.ComputeSnapshot(asOf, now, bias, active, health, OptionalReferences(asOf, now), statuses) with
            {
                OptionAnalysis = selectedOptions,
                OptionContracts = Volatile.Read(ref optionsResult)?.VerifiedContracts ?? Array.Empty<OptionContract>()
            };
            lastRawSnapshot = snapshot;
            if (changed.Count > 0 || version != seenTickVersion || health != lastHealth || optionalDirty || now.Second % 5 == 0)
            {
                var scored = LevelScoring.Build(snapshot, scoringSettings);
                publish(new(snapshot, health, optionsStatus, profileStatus, $"{source.Connection.Name} [{source.Connection.Id}]", coverage)
                    { ScoredSnapshot = scored });
            }
            optionalDirty = false;
            seenTickVersion = version;
            lastHealth = health;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!disposed) publish(new(null, "source calculation unavailable", optionsStatus, profileStatus, settings.ConnectionId, coverage, ex.Message));
        }
        finally { worker.Release(); }
    }

    private async Task InitializeAsync()
    {
        CleanupSource();
        sourceLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        publish(new(null, "loading NQZ6", optionsStatus, profileStatus, settings.ConnectionId, coverage));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(45));
        calendar = new SessionCalendar(string.IsNullOrWhiteSpace(settings.CalendarPath) ? null : settings.CalendarPath);
        var resolved = await Task.Run(() => SourceResolver.Resolve(settings.ConnectionId, settings.ContractId, request.Token), request.Token).ConfigureAwait(false);
        request.Token.ThrowIfCancellationRequested();
        source = resolved;
        resolved.Symbol.NewLast += OnLast;
        resolved.Connection.StateChanged += OnConnectionStateChanged;
        var loaded = await Task.Run(() => resolved.Symbol.GetHistory(new HistoryRequestParameters
        {
            Symbol = resolved.Symbol,
            FromTime = DateTime.UtcNow.AddDays(-settings.Engine.LookbackDays),
            // An unspecified right boundary is the installed SDK's live-history mode.
            ToTime = default,
            Aggregation = new HistoryAggregationTime(Period.MIN1, HistoryType.Last),
            CancellationToken = request.Token
        }), request.Token).ConfigureAwait(false);
        if (request.IsCancellationRequested || disposed) { loaded?.Dispose(); request.Token.ThrowIfCancellationRequested(); return; }
        if (loaded == null) throw new InvalidOperationException("Independent NQZ6 history request returned no data.");
        if (loaded.Symbol?.Id != resolved.Identity.ContractId || loaded.Symbol.ConnectionId != resolved.Identity.ConnectionId)
        { loaded.Dispose(); throw new InvalidOperationException("Independent history returned a different contract/connection; source suppressed."); }
        history = loaded;
        loaded.NewHistoryItem += OnHistory;
        loaded.HistoryItemUpdated += OnHistory;
        var bars = new List<SourceBar>();
        var bootstrapCount = loaded.Count;
        if (bootstrapCount > (settings.Engine.LookbackDays + 2) * 1440)
            throw new InvalidOperationException("Source history exceeds the bounded M1 lookback request; suppressed.");
        for (var i = 0; i < bootstrapCount; i++)
        {
            request.Token.ThrowIfCancellationRequested();
            if (loaded[i, SeekOriginHistory.Begin] is HistoryItemBar bar) bars.Add(Copy(bar));
        }
        if (bars.Count == 0) throw new InvalidOperationException("NQZ6 Last/M1 history is empty; check Rithmic permissions and history availability.");
        bars = bars.OrderBy(b => b.StartUtc).ToList(); // Stable ties retain the SDK's newest repeated observation last.
        var sdkTime = Utc(resolved.Symbol.LastDateTime);
        lock (quoteGate)
        {
            if (double.IsFinite(resolved.Symbol.Last) && resolved.Symbol.Last > 0 && sdkTime > observedUtc && sdkTime <= DateTime.UtcNow.AddSeconds(5))
            { latestPrice = resolved.Symbol.Last; observedUtc = sdkTime; }
            if (observedUtc == default)
            { observedUtc = bars[^1].StartUtc; latestPrice = bars[^1].Close; }
        }
        engine = new LevelEngine(resolved.Identity, calendar, settings.Engine);
        engine.UpdateBars(bars, observedUtc, DateTime.UtcNow);
        barCount = bars.Count;
        firstBarUtc = bars[0].StartUtc; lastBarUtc = bars[^1].StartUtc;
        coverage = $"{barCount:N0} initial M1 bars, {firstBarUtc:MM-dd HH:mm}–{lastBarUtc:MM-dd HH:mm} UTC; gaps checked by provider; VWAP proxy. {calendar.Status}";
        optionsStatus = settings.Options ? "options loading / underlying verification required" : "options disabled";
        profileStatus = settings.Profile ? "volume profile awaiting complete previous RTH" : "volume profile disabled";
        initialized = true;
    }

    private void OnLast(Symbol symbol, Last last)
    {
        if (disposed || source?.Symbol != symbol || !double.IsFinite(last.Price) || last.Price <= 0) return;
        var time = Utc(last.Time);
        if (time == default || time > DateTime.UtcNow.AddSeconds(5)) return;
        lock (quoteGate)
        {
            if (time < observedUtc) return;
            latestPrice = last.Price; observedUtc = time; tickVersion++;
        }
        // The owned history builds M1 volume. Adding Last size here would double count it.
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        if (!disposed && ReferenceEquals(sender, source?.Connection)) reinitializeRequested = true;
    }

    private void OnHistory(object sender, HistoryEventArgs args)
    {
        if (disposed || !ReferenceEquals(sender, history) || args.HistoryItem is not HistoryItemBar bar) return;
        var copied = Copy(bar);
        pending[copied.StartUtc] = copied;
        lock (quoteGate)
        {
            if (copied.StartUtc > observedUtc && copied.StartUtc <= DateTime.UtcNow)
            { observedUtc = copied.StartUtc; latestPrice = copied.Close; tickVersion++; }
        }
    }

    private SourceBar Copy(HistoryItemBar bar) => new(MinuteUtc(bar.TimeLeft), bar.Open, bar.High, bar.Low, bar.Close,
        source?.Symbol.VolumeType == SymbolVolumeType.Volume && double.IsFinite(bar.Volume) && bar.Volume >= 0 ? bar.Volume : double.NaN);
    private static DateTime MinuteUtc(DateTime value)
    {
        var utc = Utc(value);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }
    internal static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    private void StartOptionalRequests(DateTime asOf, DateTime now)
    {
        var captured = source;
        if (captured == null || calendar == null || captured.Connection.State != ConnectionState.Connected) return;
        var currentEpoch = Interlocked.Read(ref epoch);
        var requestToken = sourceLifetime!.Token;
        if (settings.Options && latestPrice is double price && now >= nextOptionsUtc && Interlocked.CompareExchange(ref optionBusy, 1, 0) == 0)
        {
            nextOptionsUtc = now.AddSeconds(settings.OptionRefreshSeconds);
            var halfWidth = ScanHalfWidth();
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await OptionalProviders.GetOptionsAsync(captured.Symbol, captured.Identity, price, asOf,
                        new OptionsProviderSettings(settings.OptionSeries, concentrationSettings.SelectedStrikesEachSide)
                            { BandHalfWidth = halfWidth }, requestToken).ConfigureAwait(false);
                    if (!disposed && currentEpoch == Interlocked.Read(ref epoch))
                    { Volatile.Write(ref optionsResult, result); optionsStatus = result.Status; optionalDirty = true; }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (!disposed && currentEpoch == Interlocked.Read(ref epoch)) { optionsStatus = $"options unavailable: {ex.Message}"; optionalDirty = true; } }
                finally { Interlocked.Exchange(ref optionBusy, 0); }
            });
        }
        if (!settings.Profile || now < nextProfileUtc || Volatile.Read(ref profileBusy) != 0) return;
        var currentDate = calendar.GetTradingDate(asOf);
        var currentSession = calendar.GetSession(currentDate);
        var session = currentSession.CashInterval is { } currentCash && currentCash.EndUtc <= asOf
            ? currentSession : calendar.GetSession(calendar.PreviousCashTradingDate(currentDate));
        if (!session.IsCertain) { profileStatus = $"volume profile unavailable: {session.UncertaintyReason}"; return; }
        var previous = session.CashInterval;
        if (previous is not { } interval) return;
        if (interval.EndUtc > asOf) return;
        var cached = Volatile.Read(ref profileResult);
        if (profileSession == interval.StartUtc && (cached?.Profile.Available == true || cached?.Retryable == false)) return;
        if (Interlocked.CompareExchange(ref profileBusy, 1, 0) != 0) return;
        if (profileSession != interval.StartUtc) profileWasCorrected = false;
        profileSession = interval.StartUtc;
        profileSessionEnd = interval.EndUtc;
        nextProfileUtc = now.AddMinutes(2);
        profileStatus = "volume profile loading independent previous RTH price levels";
        var currentProfileRevision = Interlocked.Read(ref profileRevision);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await OptionalProviders.GetPreviousRthProfileAsync(captured.Symbol, captured.Identity,
                    interval.StartUtc, interval.EndUtc, asOf,
                    new(new ProfileSettings(settings.ProfileBinTicks, 1, settings.ProfileNeighborhood,
                        settings.ProfileDisplayLimit, settings.ProfileDisplayLimit)), requestToken).ConfigureAwait(false);
                if (!disposed && currentEpoch == Interlocked.Read(ref epoch) && currentProfileRevision == Interlocked.Read(ref profileRevision))
                { Volatile.Write(ref profileResult, result); profileStatus = result.Status; optionalDirty = true; }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!disposed && currentEpoch == Interlocked.Read(ref epoch)) { profileStatus = $"volume profile unavailable: {ex.Message}"; optionalDirty = true; } }
            finally { Interlocked.Exchange(ref profileBusy, 0); }
        });
    }

    private IEnumerable<CandidateLevel> OptionalReferences(DateTime asOf, DateTime now)
    {
        var identity = source?.Identity;
        var options = Volatile.Read(ref optionsResult);
        if (settings.Options && options != null && options.Source == identity)
        {
            foreach (var strike in selectedOptions?.SelectedStrikes ?? Array.Empty<OptionStrike>())
            {
                var concentration = strike.StrongestConcentration;
                var measured = concentration?.IsEligible == true;
                var staleOption = (now - options.ObservedUtc).TotalSeconds > concentrationSettings.CacheTtlSeconds ||
                    concentration != null && (now > concentration.ValidUntilUtc || now >= concentration.ExpiryUtc);
                if (concentration != null && staleOption)
                    concentration = concentration with { IsEligible = false, EffectiveConcentration = 0,
                        RejectionReasons = Array.AsReadOnly(concentration.RejectionReasons.Append("stale adapter retrieval clock").ToArray()) };
                yield return new CandidateLevel
                {
                    Id = $"option:{SnapshotBuilder.PriceTick(strike.Strike, identity!.TickSize)}", Family = LevelFamily.Options,
                    Kind = !measured ? ReferenceKind.OptionContext : concentration!.IsOpenInterestBacked
                        ? ReferenceKind.OptionConcentration : ReferenceKind.OptionActivity,
                    Label = measured ? concentration!.IsOpenInterestBacked ? "OPT(OI)" : "OPT(activity)" : "OPT(unranked context)",
                    RawPrice = strike.Strike,
                    TradingDate = calendar!.GetTradingDate(options.ObservedUtc), IntervalStartUtc = options.ObservedUtc,
                    IntervalEndUtc = strike.Contracts.Max(c => c.ExpiryUtc), OriginUtc = options.ObservedUtc,
                    KnownAtUtc = options.ObservedUtc, LastUpdateUtc = options.ObservedUtc, Frozen = true,
                    Quality = QualityFlags.VerifiedUnderlying | (staleOption ? QualityFlags.Stale : QualityFlags.None),
                    Option = strike, OptionConcentration = concentration,
                    Inactive = !measured || staleOption,
                    EvidenceAvailable = !staleOption,
                    Provenance = "Rithmic chain metadata; exact NQZ6 underlying and connection verified; retrieval observation, OI publication clock unknown"
                };
            }
        }
        var profile = Volatile.Read(ref profileResult);
        if (settings.Profile && profile != null && profile.Source == identity && profile.Profile.Available && profile.StartUtc == profileSession)
        {
            foreach (var point in profile.Profile.Points)
                yield return new CandidateLevel
                {
                    Id = point.Kind == ProfileLevelKind.Poc ? $"profile:{profile.StartUtc:yyyyMMdd}:Poc" :
                        $"profile:{profile.StartUtc:yyyyMMdd}:{point.Kind}:{SnapshotBuilder.PriceTick(point.Price, identity!.TickSize)}",
                    Family = LevelFamily.VolumeProfile, Label = $"RTH {point.Kind.ToString().ToUpperInvariant()}", RawPrice = point.Price,
                    Kind = point.Kind switch { ProfileLevelKind.Poc => ReferenceKind.Poc, ProfileLevelKind.Hvn => ReferenceKind.Hvn, _ => ReferenceKind.Lvn },
                    TradingDate = calendar!.GetTradingDate(profile.StartUtc), IntervalStartUtc = profile.StartUtc,
                    IntervalEndUtc = profile.EndUtc, OriginUtc = profile.EndUtc, KnownAtUtc = profile.ObservedUtc,
                    LastUpdateUtc = profile.ObservedUtc, Frozen = true, Profile = point,
                    Quality = QualityFlags.Complete | QualityFlags.RealVolumeAtPrice | (profileWasCorrected ? QualityFlags.Corrected : QualityFlags.None),
                    Provenance = "Independent NQZ6 previous RTH SDK CalculateVolumeProfile; price-level volume matched to Last/M1 coverage"
                };
        }
    }

    private double ScanHalfWidth()
    {
        var cfg = concentrationSettings;
        var previous = lastRawSnapshot?.References.Where(x => x.Family == LevelFamily.PreviousRth).ToArray();
        var high = previous?.FirstOrDefault(x => x.Kind == ReferenceKind.High);
        var low = previous?.FirstOrDefault(x => x.Kind == ReferenceKind.Low);
        var range = high != null && low != null ? Math.Max(0, high.RawPrice - low.RawPrice) : 0;
        return Math.Min(cfg.MaximumBandHalfWidth, Math.Max(cfg.MinimumBandHalfWidth, cfg.PreviousRthRangeMultiplier * range));
    }

    private void SelectCachedOptions(DateTime asOf, DateTime now)
    {
        var cached = Volatile.Read(ref optionsResult);
        if (!settings.Options || cached == null || source == null || latestPrice is not double price || cached.Source != source.Identity)
        { selectedOptions = null; return; }
        if (cached.ObservedUtc > asOf)
        { selectedOptions = null; optionsStatus = "options snapshot awaits next NQZ6 observation (no backdating)"; return; }
        var cfg = concentrationSettings;
        var analysisChanged = !ReferenceEquals(cached, selectedOptionsFrom) || optionsAnalysis == null ||
            analyzedSettings != cfg || analyzedDate != DateOnly.FromDateTime(asOf) || asOf >= analysisExpiryUtc;
        if (analysisChanged)
        {
            // Analyze a fixed retrieval band once per observation/settings/expiry change.
            optionsAnalysis = NqCandidateLevels.Core.OptionConcentration.Analyze(cached.VerifiedContracts, source.Identity,
                cached.ScanCenter, asOf, cfg with { MinimumBandHalfWidth = Math.Max(cfg.MinimumBandHalfWidth,
                    Math.Min(cfg.MaximumBandHalfWidth, cached.ScanHalfWidth)) }, calendar: calendar,
                knownEntitlementTruncated: cached.KnownEntitlementTruncated) with
            {
                ScannedSeriesCount = cached.ScannedSeriesIds.Count,
                ScannedSeriesIds = cached.ScannedSeriesIds,
                UnqueriedSeriesIds = cached.UnqueriedSeriesIds
            };
            selectedOptionsFrom = cached; analyzedSettings = cfg; analyzedDate = DateOnly.FromDateTime(asOf);
            analysisExpiryUtc = cached.VerifiedContracts.SelectMany(c => new[]
                {
                    c.ExpiryUtc, c.ExpiryUtc.AddDays(-7), c.ExpiryUtc.AddDays(-30),
                    (c.ObservedUtc ?? cached.ObservedUtc).AddSeconds(cfg.CacheTtlSeconds).AddTicks(1),
                    (c.OpenInterestMetric?.ObservedUtc ?? c.ObservedUtc ?? cached.ObservedUtc).AddSeconds(cfg.CacheTtlSeconds).AddTicks(1),
                    (c.VolumeMetric?.ObservedUtc ?? c.ObservedUtc ?? cached.ObservedUtc).AddSeconds(cfg.CacheTtlSeconds).AddTicks(1)
                }).Where(t => t > asOf).DefaultIfEmpty(DateTime.MaxValue).Min();
        }
        if (analysisChanged || selectedOptionsPrice != price || selectedOptions == null)
            selectedOptions = NqCandidateLevels.Core.OptionConcentration.Select(optionsAnalysis!, price, cfg);
        selectedOptionsPrice = price;
        if (cached.VerifiedContracts.Count == 0) { optionsStatus = cached.Status; return; }
        var age = now - cached.ObservedUtc;
        optionsStatus = $"{selectedOptions!.Status}; scanned {cached.ScannedSeriesIds.Count}/{cached.ScannedSeriesIds.Count + cached.UnqueriedSeriesIds.Count} series; " +
            $"retrieved {cached.ObservedUtc:HH:mm:ss} UTC, age {Math.Max(0, age.TotalSeconds):F0}s / TTL {cfg.CacheTtlSeconds}s; " +
            $"cached OI publication unknown; volume session unverified; {cached.HydrationStatus}; returned-universe coverage only";
        if (age.TotalSeconds > cfg.CacheTtlSeconds) optionsStatus += "; STALE: option evidence inactive";
        if (Math.Abs(price - cached.ScanCenter) > cached.ScanHalfWidth)
            optionsStatus += "; price outside cached band: refresh queued at metadata throttle";
        if (cached.CompatibilityStatus.Length != 0) optionsStatus += "; " + cached.CompatibilityStatus;
    }

    private void CleanupSource()
    {
        Interlocked.Increment(ref epoch);
        sourceLifetime?.Cancel(); sourceLifetime?.Dispose(); sourceLifetime = null;
        Volatile.Write(ref optionsResult, null); Volatile.Write(ref profileResult, null);
        selectedOptions = null; selectedOptionsFrom = null; selectedOptionsPrice = null;
        optionsAnalysis = null; analyzedSettings = null; lastRawSnapshot = null;
        profileSession = null; profileSessionEnd = null; nextOptionsUtc = default; nextProfileUtc = default;
        profileWasCorrected = false;
        Interlocked.Increment(ref profileRevision);
        if (source != null) { source.Symbol.NewLast -= OnLast; source.Connection.StateChanged -= OnConnectionStateChanged; }
        var owned = Interlocked.Exchange(ref history, null);
        if (owned != null)
        {
            owned.NewHistoryItem -= OnHistory; owned.HistoryItemUpdated -= OnHistory; owned.Dispose();
        }
        pending.Clear();
        source = null; engine = null; initialized = false;
        lock (quoteGate) { latestPrice = null; observedUtc = default; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        // Worker teardown is serialized with bootstrap; SDK requests receive cancellation immediately.
        _ = Task.Run(async () =>
        {
            await timer.DisposeAsync().ConfigureAwait(false);
            await worker.WaitAsync().ConfigureAwait(false);
            try { CleanupSource(); }
            finally { worker.Release(); worker.Dispose(); lifetime.Dispose(); }
        });
    }

    public void SetBias(ManualBias value) { bias = value; optionalDirty = true; }

    public void UpdateScoringAndBias(ScoringSettings scoring, ManualBias value, OptionConcentrationSettings options)
    {
        scoringSettings = scoring;
        concentrationSettings = options;
        bias = value;
        optionalDirty = true;
    }
}
