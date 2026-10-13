namespace NqCandidateLevels.Core;

/// <summary>Pure independent-source engine. Quotes only change permissions; minute/session caches own bar calculations.</summary>
public sealed class LevelEngine
{
    private sealed class SessionBucket
    {
        public SortedDictionary<DateTime, SourceBar> Bars { get; } = new();
        public Dictionary<DateTime, DateTime> LastObservedAsOf { get; } = new();
        public Dictionary<DateTime, DateTime> CorrectionAvailableUtc { get; } = new();
        public long Revision;
        public long CacheRevision = -1;
        public DateTime CacheCutoff;
        public SourceBar[] Completed = [];
    }

    private sealed record Coverage(SourceBar[] Bars, bool Complete, bool Certain, bool HasStart, string Status, DateTime? CorrectionAvailableUtc = null)
    {
        public QualityFlags Flags => (Complete ? QualityFlags.Complete : QualityFlags.Partial) |
            (Certain ? QualityFlags.None : QualityFlags.UncertainSchedule) |
            (HasStart ? QualityFlags.None : QualityFlags.MissingStart) |
            (Bars.Any(x => double.IsNaN(x.Volume)) ? QualityFlags.VolumeUnavailable : QualityFlags.None) |
            (CorrectionAvailableUtc.HasValue ? QualityFlags.Corrected : QualityFlags.None);
    }

    private readonly object gate = new();
    private readonly SourceIdentity source;
    private readonly SessionCalendar calendar;
    private readonly EngineSettings settings;
    private readonly SortedDictionary<DateOnly, SessionBucket> sessions = new();
    private long dataRevision;
    private long snapshotVersion;
    private double? latestPrice;
    private DateTime? latestPriceUtc;
    private long calculatedRevision = -1;
    private DateTime calculatedCutoff;
    private CandidateLevel[] cachedReferences = [];
    private Dictionary<string, string> cachedStatus = new();
    private long calculationPassCount;

    /// <summary>Diagnostics counter: forming updates and source-price-only changes do not rebuild session providers.</summary>
    public long CalculationPassCount { get { lock (gate) return calculationPassCount; } }

    public LevelEngine(SourceIdentity source, SessionCalendar calendar, EngineSettings? settings = null)
    {
        if (!(source.TickSize > 0) || !double.IsFinite(source.TickSize)) throw new ArgumentOutOfRangeException(nameof(source));
        this.source = source;
        this.calendar = calendar;
        this.settings = settings ?? new EngineSettings();
        if (this.settings.LookbackDays < 1 || this.settings.OpeningRangeMinutes < 1 ||
            this.settings.SwingLeftBars < 1 || this.settings.SwingRightBars < 1 || this.settings.SwingLookbackBars < 3 ||
            this.settings.SwingCountPerSide < 1) throw new ArgumentOutOfRangeException(nameof(settings));
    }

    /// <summary>Last observation for an identical UTC minute wins. Forming bars are retained but never calculated before their close.</summary>
    public void UpdateBars(IEnumerable<SourceBar> bars, DateTime asOfUtc, DateTime computedUtc)
    {
        RequireUtc(asOfUtc); RequireUtc(computedUtc);
        lock (gate)
        {
            foreach (var observation in bars)
            {
                if (observation.StartUtc.Kind != DateTimeKind.Utc) continue;
                var bar = observation with
                {
                    StartUtc = new DateTime(observation.StartUtc.Ticks - observation.StartUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc),
                    Volume = double.IsFinite(observation.Volume) && observation.Volume >= 0 ? observation.Volume : double.NaN
                };
                if (!bar.IsValid) continue;
                var date = calendar.GetTradingDate(bar.StartUtc);
                if (!sessions.TryGetValue(date, out var bucket)) sessions[date] = bucket = new SessionBucket();
                if (bucket.Bars.TryGetValue(bar.StartUtc, out var existing))
                {
                    if (existing == bar)
                    {
                        bucket.LastObservedAsOf[bar.StartUtc] = asOfUtc;
                        continue;
                    }
                    if (bucket.LastObservedAsOf.TryGetValue(bar.StartUtc, out var priorAsOf) && existing.EndUtc <= priorAsOf)
                    {
                        bucket.CorrectionAvailableUtc[bar.StartUtc] = computedUtc;
                    }
                }
                bucket.Bars[bar.StartUtc] = bar;
                bucket.LastObservedAsOf[bar.StartUtc] = asOfUtc;
                bucket.Revision++;
                if (bar.EndUtc <= asOfUtc) dataRevision++;
                if (bar.EndUtc <= asOfUtc && (!latestPriceUtc.HasValue || bar.EndUtc >= latestPriceUtc))
                {
                    latestPrice = bar.Close;
                    latestPriceUtc = bar.EndUtc;
                }
            }
            var oldestDate = calendar.GetTradingDate(asOfUtc.AddDays(-settings.LookbackDays));
            foreach (var date in sessions.Keys.Where(x => x < oldestDate).ToArray())
            {
                sessions.Remove(date);
                dataRevision++;
            }
        }
    }

    public void SetSourcePrice(double price, DateTime observedUtc)
    {
        RequireUtc(observedUtc);
        if (!double.IsFinite(price) || price <= 0) return;
        lock (gate)
        {
            if (latestPriceUtc.HasValue && observedUtc < latestPriceUtc.Value) return;
            latestPrice = price;
            latestPriceUtc = observedUtc;
        }
    }

    public LevelSnapshot ComputeSnapshot(DateTime asOfUtc, DateTime computedUtc, ManualBias bias = ManualBias.Both,
        bool sourceActive = true, string? healthReason = null, IEnumerable<CandidateLevel>? optionalReferences = null,
        IReadOnlyDictionary<string, string>? optionalStatus = null)
    {
        RequireUtc(asOfUtc); RequireUtc(computedUtc);
        lock (gate)
        {
            var cutoff = new DateTime(asOfUtc.Ticks - asOfUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            if (calculatedRevision != dataRevision || calculatedCutoff != cutoff)
            {
                Calculate(cutoff);
                calculatedRevision = dataRevision;
                calculatedCutoff = cutoff;
            }
            var status = new Dictionary<string, string>(cachedStatus)
            {
                ["Source"] = healthReason ?? (sourceActive ? "Independent NQZ6 source active" : "Source inactive"),
                ["VWAP mode"] = "Completed M1 typical-price × traded-volume proxy; exact trade coverage not claimed"
            };
            if (optionalStatus is not null) foreach (var pair in optionalStatus) status[pair.Key] = pair.Value;
            var references = optionalReferences is null ? cachedReferences : cachedReferences.Concat(optionalReferences);
            var knownPrice = latestPriceUtc <= asOfUtc ? latestPrice : null;
            var knownUpdate = latestPriceUtc <= asOfUtc ? latestPriceUtc : null;
            if (!knownPrice.HasValue)
            {
                var latestBar = CompletedBarsDescending(asOfUtc, 1).FirstOrDefault();
                if (latestBar is not null) { knownPrice = latestBar.Close; knownUpdate = latestBar.EndUtc; }
            }
            return SnapshotBuilder.Build(source, ++snapshotVersion, asOfUtc, computedUtc, knownUpdate, knownPrice,
                sourceActive, bias, references, status, settings.AtLevelToleranceTicks);
        }
    }

    private void Calculate(DateTime cutoff)
    {
        calculationPassCount++;
        var levels = new List<CandidateLevel>();
        var status = new Dictionary<string, string>();
        var date = calendar.GetTradingDate(cutoff);
        var current = calendar.GetSession(date);
        // References roll when the newest source session has actually completed at the observed market as-of.
        // During the closed weekend this preserves Friday; it never manufactures a Saturday/Sunday reference.
        var previous = current.FuturesIntervals.Count > 0 && current.FuturesIntervals.Max(x => x.EndUtc) <= cutoff
            ? current : calendar.GetSession(calendar.PreviousTradingDate(date));
        var previousCash = current.CashInterval.HasValue && current.CashInterval.Value.EndUtc <= cutoff
            ? current : calendar.GetSession(calendar.PreviousCashTradingDate(date));

        if (settings.PreviousRth)
        {
            var coverage = previousCash.CashInterval.HasValue
                ? Read(previousCash, Intersect(previousCash.FuturesIntervals, previousCash.CashInterval.Value), cutoff)
                : Empty(previousCash.IsCertain, "No cash session for reference trading date");
            status["Previous RTH"] = coverage.Status;
            if (previousCash.CashInterval.HasValue && cutoff >= previousCash.CashInterval.Value.EndUtc && Usable(coverage))
                AddHighLowClose(levels, previousCash, coverage, LevelFamily.PreviousRth, "RTH PDH", "RTH PDL", "Previous RTH close", previousCash.CashInterval.Value, true);
        }
        if (settings.PreviousFutures)
        {
            var coverage = Read(previous, previous.FuturesIntervals, cutoff);
            status["Previous futures session"] = coverage.Status;
            if (previous.FuturesIntervals.Count > 0 && cutoff >= previous.FuturesIntervals.Max(x => x.EndUtc) && Usable(coverage))
                AddHighLowClose(levels, previous, coverage, LevelFamily.PreviousFutures, "Full PDH", "Full PDL", "Previous futures close",
                    new UtcInterval(previous.FuturesIntervals.Min(x => x.StartUtc), previous.FuturesIntervals.Max(x => x.EndUtc)), true);
        }
        if (settings.Overnight)
        {
            var coverage = current.OvernightInterval.HasValue
                ? Read(current, Intersect(current.FuturesIntervals, current.OvernightInterval.Value), cutoff)
                : Empty(current.IsCertain, "Overnight window unavailable");
            status["Overnight"] = coverage.Status;
            if (current.OvernightInterval.HasValue && Usable(coverage))
            {
                var frozen = cutoff >= current.OvernightInterval.Value.EndUtc;
                AddExtremes(levels, current, coverage, LevelFamily.Overnight, "Overnight high", "Overnight low", current.OvernightInterval.Value, !frozen, frozen);
            }
        }
        if (settings.CurrentWeek) AddWeek(levels, status, date, cutoff, false);
        if (settings.PreviousWeek) AddWeek(levels, status, date, cutoff, true);
        if (settings.CurrentRth)
        {
            var coverage = current.CashInterval.HasValue
                ? Read(current, Intersect(current.FuturesIntervals, current.CashInterval.Value), cutoff)
                : Empty(current.IsCertain, "Current cash session unavailable");
            status["Current RTH"] = coverage.Status;
            if (current.CashInterval.HasValue && Usable(coverage))
            {
                var interval = current.CashInterval.Value;
                var first = coverage.Bars[0];
                levels.Add(Level(current, LevelFamily.CurrentRth, ReferenceKind.Open, "RTH open", first.Open, interval, first.StartUtc, AvailableAt(first.EndUtc, coverage),
                    UpdatedAt(first.EndUtc, coverage), false, true, coverage.Flags));
                var volume = coverage.Bars.Sum(x => x.Volume);
                if (double.IsFinite(volume) && volume > 0)
                {
                    var price = coverage.Bars.Sum(x => ((x.High + x.Low + x.Close) / 3) * x.Volume) / volume;
                    var last = coverage.Bars[^1];
                    levels.Add(Level(current, LevelFamily.CurrentRth, ReferenceKind.VwapProxy, "VWAP proxy", price, interval, interval.StartUtc, AvailableAt(last.EndUtc, coverage),
                        UpdatedAt(last.EndUtc, coverage), cutoff < interval.EndUtc, cutoff >= interval.EndUtc, coverage.Flags | QualityFlags.BarVwapProxy));
                    status["VWAP"] = $"Bar proxy through {last.EndUtc:O}; volume {volume:G}";
                }
                else status["VWAP"] = double.IsNaN(volume) ? "Unavailable: source traded volume unknown" : "Unavailable: complete bars have genuine zero traded volume";
            }
            else status["VWAP"] = "Unavailable: current RTH start or completed-minute coverage missing";
        }
        if (settings.OpeningRange)
        {
            if (current.CashInterval.HasValue)
            {
                var cash = current.CashInterval.Value;
                var range = new UtcInterval(cash.StartUtc, new[] { cash.StartUtc.AddMinutes(settings.OpeningRangeMinutes), cash.EndUtc }.Min());
                var coverage = Read(current, Intersect(current.FuturesIntervals, range), cutoff);
                var completed = cutoff >= range.EndUtc;
                status["Opening range"] = completed ? coverage.Status : $"Developing until {range.EndUtc:O}; display {(settings.ShowDevelopingOpeningRange ? "enabled" : "disabled")}";
                if (Usable(coverage) && (completed || settings.ShowDevelopingOpeningRange))
                    AddExtremes(levels, current, coverage, LevelFamily.OpeningRange, completed ? "OR high" : "Developing OR high",
                        completed ? "OR low" : "Developing OR low", range, !completed, completed);
            }
            else status["Opening range"] = "Unavailable: cash schedule unavailable";
        }
        if (settings.Structure) AddSwings(levels, status, cutoff);
        cachedReferences = levels.ToArray();
        cachedStatus = status;
    }

    private void AddWeek(List<CandidateLevel> levels, Dictionary<string, string> status, DateOnly date, DateTime cutoff, bool previous)
    {
        var weekStart = calendar.GetWeekStart(date).AddDays(previous ? -7 : 0);
        var schedules = Enumerable.Range(0, 5).Select(i => calendar.GetSession(weekStart.AddDays(i))).Where(x => x.IsTradingDay).ToArray();
        var relevant = schedules.Where(x => x.FuturesIntervals.Any(i => i.StartUtc < cutoff)).ToArray();
        var coverageParts = relevant.Select(x => Read(x, x.FuturesIntervals, cutoff)).ToArray();
        var name = previous ? "Previous week" : "Current week";
        var weekEnded = schedules.Length > 0 && schedules.SelectMany(x => x.FuturesIntervals).Any() &&
            cutoff >= schedules.SelectMany(x => x.FuturesIntervals).Max(x => x.EndUtc);
        if (coverageParts.Length == 0 || coverageParts.Any(x => !Usable(x)) || (previous && !weekEnded) || schedules.Any(x => !x.IsCertain))
        {
            status[name] = "Unavailable: incomplete or uncertain exchange-week coverage";
            return;
        }
        var bars = coverageParts.SelectMany(x => x.Bars).OrderBy(x => x.StartUtc).ToArray();
        var interval = new UtcInterval(relevant.SelectMany(x => x.FuturesIntervals).Min(x => x.StartUtc), schedules.SelectMany(x => x.FuturesIntervals).Max(x => x.EndUtc));
        var synthetic = relevant[0];
        var correctionAvailable = coverageParts.Where(x => x.CorrectionAvailableUtc.HasValue)
            .Select(x => x.CorrectionAvailableUtc).DefaultIfEmpty(null).Max();
        var coverage = new Coverage(bars, true, true, true, "Complete expected completed-minute coverage", correctionAvailable);
        AddExtremes(levels, synthetic, coverage, previous ? LevelFamily.PreviousWeek : LevelFamily.CurrentWeek,
            previous ? "Previous week high" : "Week high", previous ? "Previous week low" : "Week low", interval, !previous && !weekEnded, previous || weekEnded);
        if (!previous)
        {
            var first = bars[0];
            levels.Add(Level(synthetic, LevelFamily.CurrentWeek, ReferenceKind.Open, "Week open", first.Open, interval, first.StartUtc, AvailableAt(first.EndUtc, coverage),
                UpdatedAt(first.EndUtc, coverage), false, true, coverage.Flags));
        }
        status[name] = $"Complete coverage of {coverageParts.Length} known exchange sessions; week of {weekStart}";
    }

    private void AddSwings(List<CandidateLevel> levels, Dictionary<string, string> status, DateTime cutoff)
    {
        var bars = CompletedBarsDescending(cutoff, settings.SwingLookbackBars + settings.SwingLeftBars + settings.SwingRightBars)
            .Reverse().ToArray();
        var highs = new List<CandidateLevel>();
        var lows = new List<CandidateLevel>();
        for (var i = settings.SwingLeftBars; i < bars.Length - settings.SwingRightBars; i++)
        {
            var start = i - settings.SwingLeftBars;
            var end = i + settings.SwingRightBars;
            if (bars[end].StartUtc - bars[start].StartUtc != TimeSpan.FromMinutes(end - start)) continue;
            var pivot = bars[i];
            var schedule = calendar.GetSession(calendar.GetTradingDate(pivot.StartUtc));
            if (!schedule.IsCertain || !schedule.FuturesIntervals.Any(x => x.Contains(pivot.StartUtc))) continue;
            // Equal pivots use the earliest observation: strict on the left, inclusive on the right.
            var high = Enumerable.Range(start, settings.SwingLeftBars).All(j => pivot.High > bars[j].High) &&
                Enumerable.Range(i + 1, settings.SwingRightBars).All(j => pivot.High >= bars[j].High);
            var low = Enumerable.Range(start, settings.SwingLeftBars).All(j => pivot.Low < bars[j].Low) &&
                Enumerable.Range(i + 1, settings.SwingRightBars).All(j => pivot.Low <= bars[j].Low);
            var interval = new UtcInterval(bars[start].StartUtc, bars[end].EndUtc);
            var correction = Enumerable.Range(start, end - start + 1).Select(j => CorrectionAvailability(bars[j]))
                .Where(x => x.HasValue).DefaultIfEmpty(null).Max();
            var knownAt = correction.HasValue && correction > bars[end].EndUtc ? correction.Value : bars[end].EndUtc;
            var quality = QualityFlags.Complete | (correction.HasValue ? QualityFlags.Corrected : QualityFlags.None);
            if (high) highs.Add(Level(schedule, LevelFamily.Structure, ReferenceKind.SwingHigh, "Confirmed swing high", pivot.High, interval, pivot.StartUtc,
                knownAt, knownAt, false, true, quality, $":{pivot.StartUtc.Ticks}"));
            if (low) lows.Add(Level(schedule, LevelFamily.Structure, ReferenceKind.SwingLow, "Confirmed swing low", pivot.Low, interval, pivot.StartUtc,
                knownAt, knownAt, false, true, quality, $":{pivot.StartUtc.Ticks}"));
        }
        levels.AddRange(highs.TakeLast(settings.SwingCountPerSide));
        levels.AddRange(lows.TakeLast(settings.SwingCountPerSide));
        status["Structure"] = $"{Math.Min(highs.Count, settings.SwingCountPerSide)} highs, {Math.Min(lows.Count, settings.SwingCountPerSide)} lows; {settings.SwingLeftBars}/{settings.SwingRightBars} completed bars, earliest equal pivot wins";
    }

    private SourceBar[] GetCompleted(SessionSchedule schedule, DateTime cutoff)
    {
        if (!sessions.TryGetValue(schedule.TradingDate, out var bucket)) return [];
        var effective = schedule.FuturesIntervals.Count > 0 ? new[] { cutoff, schedule.FuturesIntervals.Max(x => x.EndUtc) }.Min() : cutoff;
        if (bucket.CacheRevision != bucket.Revision || bucket.CacheCutoff != effective)
        {
            bucket.Completed = bucket.Bars.Values.Where(x => x.EndUtc <= effective && schedule.FuturesIntervals.Any(i => i.Contains(x.StartUtc) && x.EndUtc <= i.EndUtc)).ToArray();
            bucket.CacheRevision = bucket.Revision;
            bucket.CacheCutoff = effective;
        }
        return bucket.Completed;
    }

    private Coverage Read(SessionSchedule schedule, IReadOnlyList<UtcInterval> intervals, DateTime cutoff)
    {
        if (!schedule.IsCertain) return Empty(false, schedule.UncertaintyReason ?? "Uncertain schedule; suppressed");
        var expected = intervals.Where(x => x.StartUtc < cutoff).Select(x => new UtcInterval(x.StartUtc,
            new[] { x.EndUtc, cutoff }.Min())).Where(x => x.Duration >= TimeSpan.FromMinutes(1)).ToArray();
        if (expected.Length == 0) return Empty(true, "Unavailable: no completed minute in reference interval");
        var bars = GetCompleted(schedule, cutoff).Where(x => expected.Any(i => i.Contains(x.StartUtc) && x.EndUtc <= i.EndUtc)).ToArray();
        var lookup = bars.Select(x => x.StartUtc).ToHashSet();
        var missing = 0;
        foreach (var interval in expected)
            for (var minute = interval.StartUtc; minute.AddMinutes(1) <= interval.EndUtc; minute = minute.AddMinutes(1))
                if (!lookup.Contains(minute)) missing++;
        var hasStart = lookup.Contains(expected[0].StartUtc);
        var correctionAvailable = bars.Select(CorrectionAvailability).Where(x => x.HasValue).DefaultIfEmpty(null).Max();
        return new Coverage(bars, missing == 0 && bars.Length > 0, true, hasStart,
            missing == 0 && bars.Length > 0 ? $"Complete expected coverage; {bars.Length} completed M1 bars" :
                $"Unavailable: {missing} missing completed M1 bars{(hasStart ? "" : "; interval start missing")}", correctionAvailable);
    }

    private IEnumerable<SourceBar> CompletedBarsDescending(DateTime cutoff, int count)
    {
        var remaining = count;
        foreach (var pair in sessions.Reverse())
        {
            var schedule = calendar.GetSession(pair.Key);
            foreach (var bar in GetCompleted(schedule, cutoff).Reverse())
            {
                if (remaining-- <= 0) yield break;
                yield return bar;
            }
        }
    }

    private static IReadOnlyList<UtcInterval> Intersect(IReadOnlyList<UtcInterval> intervals, UtcInterval window) =>
        intervals.Select(x => x.Intersect(window)).Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x.StartUtc).ToArray();
    private static Coverage Empty(bool certain, string reason) => new([], false, certain, false, reason);
    private static bool Usable(Coverage coverage) => coverage.Certain && coverage.Complete && coverage.Bars.Length > 0 && coverage.HasStart;

    private void AddHighLowClose(List<CandidateLevel> levels, SessionSchedule schedule, Coverage coverage, LevelFamily family,
        string highLabel, string lowLabel, string closeLabel, UtcInterval interval, bool frozen)
    {
        AddExtremes(levels, schedule, coverage, family, highLabel, lowLabel, interval, false, frozen);
        var last = coverage.Bars[^1];
        levels.Add(Level(schedule, family, ReferenceKind.Close, closeLabel, last.Close, interval, last.StartUtc, AvailableAt(interval.EndUtc, coverage),
            UpdatedAt(last.EndUtc, coverage), false, frozen, coverage.Flags));
    }

    private void AddExtremes(List<CandidateLevel> levels, SessionSchedule schedule, Coverage coverage, LevelFamily family,
        string highLabel, string lowLabel, UtcInterval interval, bool developing, bool frozen)
    {
        var high = coverage.Bars.OrderByDescending(x => x.High).ThenBy(x => x.StartUtc).First();
        var low = coverage.Bars.OrderBy(x => x.Low).ThenBy(x => x.StartUtc).First();
        var lastUpdate = UpdatedAt(coverage.Bars[^1].EndUtc, coverage);
        // A completed aggregate is only available after its entire source interval; a developing aggregate is known at its latest completed bar.
        var knownAt = AvailableAt(frozen ? interval.EndUtc : coverage.Bars[^1].EndUtc, coverage);
        levels.Add(Level(schedule, family, ReferenceKind.High, highLabel, high.High, interval, high.StartUtc, knownAt, lastUpdate, developing, frozen, coverage.Flags));
        levels.Add(Level(schedule, family, ReferenceKind.Low, lowLabel, low.Low, interval, low.StartUtc, knownAt, lastUpdate, developing, frozen, coverage.Flags));
    }

    private CandidateLevel Level(SessionSchedule schedule, LevelFamily family, ReferenceKind kind, string label, double price, UtcInterval interval,
        DateTime origin, DateTime knownAt, DateTime lastUpdate, bool developing, bool frozen, QualityFlags quality, string idSuffix = "") => new()
    {
        Id = $"{family}:{schedule.TradingDate:yyyyMMdd}:{kind}{idSuffix}", Family = family, Kind = kind, Label = label, RawPrice = price,
        OriginatingEventId = $"{source.ConnectionId}:{source.ContractId}:M1:{origin.Ticks}:{EventKind(kind)}:{SnapshotBuilder.PriceTick(price, source.TickSize)}",
        TradingDate = schedule.TradingDate, IntervalStartUtc = interval.StartUtc, IntervalEndUtc = interval.EndUtc,
        OriginUtc = origin, KnownAtUtc = knownAt, LastUpdateUtc = lastUpdate, Developing = developing, Frozen = frozen,
        Quality = quality
    };

    private static ReferenceKind EventKind(ReferenceKind kind) => kind switch
    {
        ReferenceKind.SwingHigh => ReferenceKind.High,
        ReferenceKind.SwingLow => ReferenceKind.Low,
        _ => kind
    };

    private DateTime? CorrectionAvailability(SourceBar bar) =>
        sessions.TryGetValue(calendar.GetTradingDate(bar.StartUtc), out var bucket) && bucket.CorrectionAvailableUtc.TryGetValue(bar.StartUtc, out var correction)
            ? correction : null;
    private static DateTime AvailableAt(DateTime ordinary, Coverage coverage) =>
        coverage.CorrectionAvailableUtc.HasValue && coverage.CorrectionAvailableUtc > ordinary ? coverage.CorrectionAvailableUtc.Value : ordinary;
    private static DateTime UpdatedAt(DateTime ordinary, Coverage coverage) => AvailableAt(ordinary, coverage);

    private static void RequireUtc(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc) throw new ArgumentException("Calculation times must be UTC.");
    }
}
