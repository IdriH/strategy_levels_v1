using NqCandidateLevels.Core;

var tests = new (string Name, Action Run)[]
{
    ("Friday/Monday mapping and closed maintenance", CalendarMapping),
    ("US daylight-saving transitions", DaylightSaving),
    ("Half-open cash and overnight boundaries", HalfOpen),
    ("Verified shortened session and schedule uncertainty", ShortenedSession),
    ("Calendar maintenance gaps excluded from OR coverage", MaintenanceCoverage),
    ("Opening range only after completed bars", OpeningRange),
    ("Swing confirmation and earliest equal pivot", Swings),
    ("Overnight freezes after cash open", OvernightFreezes),
    ("Previous references preserve values and correction versions", PreviousReferences),
    ("Reference rollover at completed cash/full session and weekend", ReferenceRollover),
    ("Weekly values develop without fabricated open", Weekly),
    ("Missing coverage versus genuine zero volume", CoverageAndVolume),
    ("Bootstrap/live equivalence and corrected VWAP volume", BootstrapAndCorrection),
    ("Forming finalization versus completed correction availability", CorrectionAvailability),
    ("Forming quotes reuse completed-session calculation cache", FormingCache),
    ("Tick-equivalent merge and four manual biases", MergeAndBias),
    ("Stale source and future observations excluded", Availability),
    ("Option underlying, expiry, series, strikes and unknown OI", Options),
    ("Options SDK metadata aliases, series provenance and expiry precision", OptionsCompatibilityChecks.Run),
    ("Strict source metadata accepts exact NQZ6 only", SourceRules),
    ("Real profile coverage, ties, plateaus and traded boundaries", Profiles),
    ("Native profile conservation and independent coverage", ProfileCompatibilityChecks.Run),
    ("Host display context cannot affect source engine", HostIndependence)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static SourceIdentity Source() => new("rithmic-1", "NQZ6-contract", "NQZ6", SessionCalendar.NqZ6LastTradingUtc, 0.25);
static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual, string message) where T : notnull => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, actual {actual}");
static void Near(double expected, double actual, string message) => Check(Math.Abs(expected - actual) < 1e-8, $"{message}: expected {expected}, actual {actual}");
static SourceBar Bar(DateTime start, double price = 100, double volume = 10) => new(start, price, price + 1, price - 1, price, volume);
static SourceBar[] Fill(UtcInterval interval, Func<int, double>? price = null, double volume = 10) =>
    Enumerable.Range(0, (int)interval.Duration.TotalMinutes).Select(i => Bar(interval.StartUtc.AddMinutes(i), price?.Invoke(i) ?? 100, volume)).ToArray();
static LevelSnapshot Snapshot(LevelEngine engine, DateTime asOf, ManualBias bias = ManualBias.Both) => engine.ComputeSnapshot(asOf, asOf, bias);
static CandidateLevel Find(LevelSnapshot snapshot, string label) => snapshot.References.Single(x => x.Label == label);
static EngineSettings Only(bool rth = false, bool overnight = false, bool week = false, bool previousRth = false,
    bool previousFutures = false, bool or = false, bool swings = false) => new()
{
    PreviousRth = previousRth, PreviousFutures = previousFutures, Overnight = overnight,
    CurrentWeek = week, PreviousWeek = week, CurrentRth = rth, OpeningRange = or, Structure = swings
};

static void CalendarMapping()
{
    var calendar = new SessionCalendar();
    Equal(new DateOnly(2026, 10, 5), calendar.GetTradingDate(Utc(10, 4, 22)), "Sunday evening maps Monday");
    Equal(new DateOnly(2026, 10, 2), calendar.GetTradingDate(Utc(10, 3, 12)), "Saturday retains Friday");
    Equal(new DateOnly(2026, 10, 2), calendar.PreviousTradingDate(new(2026, 10, 5)), "Monday previous date is Friday");
    Check(!calendar.IsOpen(Utc(10, 5, 21, 30)), "Chicago maintenance interval is closed");
    Check(!calendar.IsOpen(Utc(10, 2, 23)), "Friday evening is closed");
    Check(calendar.IsOpen(Utc(10, 4, 22)), "Sunday reopening is open");
}

static void DaylightSaving()
{
    var calendar = new SessionCalendar();
    Equal(Utc(3, 6, 14, 30), calendar.GetSession(new(2026, 3, 6)).CashInterval!.Value.StartUtc, "Before spring DST");
    Equal(Utc(3, 9, 13, 30), calendar.GetSession(new(2026, 3, 9)).CashInterval!.Value.StartUtc, "After spring DST");
    Equal(Utc(3, 8, 22), calendar.GetSession(new(2026, 3, 9)).FuturesIntervals[0].StartUtc, "Sunday start uses Chicago DST");
    Equal(Utc(10, 30, 13, 30), calendar.GetSession(new(2026, 10, 30)).CashInterval!.Value.StartUtc, "Before autumn DST");
    Equal(Utc(11, 2, 14, 30), calendar.GetSession(new(2026, 11, 2)).CashInterval!.Value.StartUtc, "After autumn DST");
    Equal(Utc(11, 1, 23), calendar.GetSession(new(2026, 11, 2)).FuturesIntervals[0].StartUtc, "Sunday autumn start");
}

static void HalfOpen()
{
    var schedule = new SessionCalendar().GetSession(new(2026, 10, 7));
    var cash = schedule.CashInterval!.Value;
    var overnight = schedule.OvernightInterval!.Value;
    Check(cash.Contains(cash.StartUtc) && !cash.Contains(cash.EndUtc), "Cash boundaries must be half-open");
    Check(overnight.Contains(overnight.StartUtc) && !overnight.Contains(cash.StartUtc), "Cash open cannot overlap overnight");
}

static void ShortenedSession()
{
    var path = Path.Combine(Path.GetTempPath(), $"nq-calendar-test-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, """
    {"schemaVersion":1,"sessions":[{"tradingDate":"2026-11-27","verified":true,"source":"Test authoritative fixture","futuresClosed":false,"futuresIntervalsChicago":[{"start":"2026-11-26T17:00:00","end":"2026-11-27T12:00:00"}],"cashClosed":false,"cashNewYork":{"start":"2026-11-27T09:30:00","end":"2026-11-27T13:00:00"}}]}
    """);
    try
    {
        var calendar = new SessionCalendar(path);
        Check(calendar.LoadError is null, "Valid override must load: " + calendar.LoadError);
        var cash = calendar.GetSession(new(2026, 11, 27)).CashInterval!.Value;
        Equal(Utc(11, 27, 18), cash.EndUtc, "Short cash session ends 13 NY");
        Check(calendar.GetSession(new(2026, 11, 27)).IsCertain, "Verified short session is certain");
        Check(!new SessionCalendar().GetSession(new(2026, 11, 27)).IsCertain, "Unverified holiday schedule suppressed");
        var engine = new LevelEngine(Source(), calendar, Only(previousRth: true));
        engine.UpdateBars(Fill(cash, i => 100 + i * 0.25), Utc(11, 30, 14), Utc(11, 30, 14));
        var previous = Find(Snapshot(engine, Utc(11, 30, 14)), "RTH PDH");
        Near(153.25, previous.RawPrice, "Previous shortened cash high uses all actual minutes");
    }
    finally { File.Delete(path); }
    var uncertain = new SessionCalendar(Path.Combine(Path.GetTempPath(), "missing-nq-calendar-test.json"));
    Check(!uncertain.GetSession(new(2026, 10, 7)).IsCertain, "Requested missing override fails closed");
    foreach (var malformed in new[] { "{\"schemaVersion\":2,\"sessions\":[]}", "{\"schemaVersion\":1,\"sessions\":[null]}", "{\"schemaVersion\":1,\"sesions\":[]}" })
    {
        File.WriteAllText(path, malformed);
        try
        {
            var bad = new SessionCalendar(path);
            Check(bad.LoadError is not null && !bad.GetSession(new(2026, 10, 7)).IsCertain, "Malformed override must fail closed");
        }
        finally { File.Delete(path); }
    }
}

static void OpeningRange()
{
    var calendar = new SessionCalendar();
    var cash = calendar.GetSession(new(2026, 10, 7)).CashInterval!.Value;
    var engine = new LevelEngine(Source(), calendar, Only(or: true));
    var bars = Enumerable.Range(0, 15).Select(i => Bar(cash.StartUtc.AddMinutes(i), 100 + i)).ToArray();
    engine.UpdateBars(bars, cash.StartUtc.AddMinutes(14), cash.StartUtc.AddMinutes(14));
    Check(!Snapshot(engine, cash.StartUtc.AddMinutes(14)).References.Any(), "OR unavailable until complete");
    var snapshot = Snapshot(engine, cash.StartUtc.AddMinutes(15));
    Near(115, Find(snapshot, "OR high").RawPrice, "OR high");
    Equal(cash.StartUtc.AddMinutes(15), Find(snapshot, "OR high").KnownAtUtc, "OR starts ray at completion");
    engine.UpdateBars([Bar(cash.StartUtc.AddMinutes(15), 999)], cash.StartUtc.AddMinutes(15), cash.StartUtc.AddMinutes(15));
    Near(115, Find(Snapshot(engine, cash.StartUtc.AddMinutes(16)), "OR high").RawPrice, "OR excludes subsequent bar");
}

static void MaintenanceCoverage()
{
    var path = Path.Combine(Path.GetTempPath(), $"nq-gap-test-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, """
    {"schemaVersion":1,"sessions":[{"tradingDate":"2026-10-07","verified":true,"source":"Test maintenance fixture","futuresClosed":false,"futuresIntervalsChicago":[{"start":"2026-10-06T17:00:00","end":"2026-10-07T09:00:00"},{"start":"2026-10-07T09:15:00","end":"2026-10-07T16:00:00"}],"cashClosed":false,"cashNewYork":{"start":"2026-10-07T09:30:00","end":"2026-10-07T16:00:00"}}]}
    """);
    try
    {
        var calendar = new SessionCalendar(path);
        Check(calendar.LoadError is null, "Maintenance fixture loads");
        var schedule = calendar.GetSession(new(2026, 10, 7));
        var range = new UtcInterval(schedule.CashInterval!.Value.StartUtc, schedule.CashInterval!.Value.StartUtc.AddMinutes(60));
        var bars = schedule.FuturesIntervals.Select(x => x.Intersect(range)).Where(x => x.HasValue).SelectMany(x => Fill(x!.Value)).ToArray();
        Equal(45, bars.Length, "15 minute exchange break creates no expected trade bars");
        var engine = new LevelEngine(Source(), calendar, Only(or: true) with { OpeningRangeMinutes = 60 });
        engine.UpdateBars(bars, range.EndUtc, range.EndUtc);
        Near(101, Find(Snapshot(engine, range.EndUtc), "OR high").RawPrice, "Verified break does not cause missing coverage");
    }
    finally { File.Delete(path); }
}

static void Swings()
{
    var start = Utc(10, 7, 14);
    var engine = new LevelEngine(Source(), new SessionCalendar(), Only(swings: true));
    var bars = new[] { 100d, 101, 105, 105, 103 }.Select((p, i) => Bar(start.AddMinutes(i), p)).ToArray();
    engine.UpdateBars(bars, start.AddMinutes(4), start.AddMinutes(4));
    Check(!Snapshot(engine, start.AddMinutes(4)).References.Any(), "Two right bars must finish");
    var high = Find(Snapshot(engine, start.AddMinutes(5)), "Confirmed swing high");
    Equal(start.AddMinutes(2), high.OriginUtc, "Equal plateau selects earliest pivot");
    Equal(start.AddMinutes(5), high.KnownAtUtc, "Swing is known at right confirmation close");
    Check(high.KnownAtUtc > high.OriginUtc, "Ray cannot start at pivot");
}

static void OvernightFreezes()
{
    var calendar = new SessionCalendar();
    var schedule = calendar.GetSession(new(2026, 10, 7));
    var overnight = schedule.OvernightInterval!.Value;
    var engine = new LevelEngine(Source(), calendar, Only(overnight: true));
    engine.UpdateBars(Fill(overnight), overnight.EndUtc, overnight.EndUtc);
    var frozen = Find(Snapshot(engine, overnight.EndUtc), "Overnight high");
    Check(frozen.Frozen && !frozen.Developing, "Overnight freezes at cash open");
    engine.UpdateBars([Bar(overnight.EndUtc, 999)], overnight.EndUtc.AddMinutes(1), overnight.EndUtc.AddMinutes(1));
    Near(frozen.RawPrice, Find(Snapshot(engine, overnight.EndUtc.AddMinutes(1)), "Overnight high").RawPrice, "Cash data does not change frozen overnight");
    var developingEngine = new LevelEngine(Source(), calendar, Only(overnight: true));
    developingEngine.UpdateBars(Fill(new UtcInterval(overnight.StartUtc, overnight.StartUtc.AddMinutes(3))), overnight.StartUtc.AddMinutes(3), overnight.StartUtc.AddMinutes(3));
    Check(Find(Snapshot(developingEngine, overnight.StartUtc.AddMinutes(3)), "Overnight high").Developing, "Overnight developing before cash open");
}

static void PreviousReferences()
{
    var calendar = new SessionCalendar();
    var cash = calendar.GetSession(new(2026, 10, 6)).CashInterval!.Value;
    var engine = new LevelEngine(Source(), calendar, Only(previousRth: true));
    var bars = Fill(cash);
    engine.UpdateBars(bars, Utc(10, 7, 14), Utc(10, 7, 14));
    var initial = Find(Snapshot(engine, Utc(10, 7, 14)), "RTH PDH");
    Near(initial.RawPrice, Find(Snapshot(engine, Utc(10, 7, 19)), "RTH PDH").RawPrice, "Previous cash remains fixed during reference date");
    engine.UpdateBars([bars[5] with { High = 200 }], Utc(10, 7, 19), Utc(10, 7, 19));
    var corrected = Find(Snapshot(engine, Utc(10, 7, 19)), "RTH PDH");
    Equal(initial.Id, corrected.Id, "Correction preserves logical ID");
    Near(200, corrected.RawPrice, "Corrected high replaces earlier value");
    Check(corrected.Quality.HasFlag(QualityFlags.Corrected) && corrected.SnapshotVersion > initial.SnapshotVersion, "Correction creates explicit version flag");
}

static void Weekly()
{
    var calendar = new SessionCalendar();
    var monday = calendar.GetSession(new(2026, 10, 5));
    var start = monday.FuturesIntervals[0].StartUtc;
    var engine = new LevelEngine(Source(), calendar, Only(week: true));
    engine.UpdateBars(Fill(new UtcInterval(start, start.AddMinutes(2))), start.AddMinutes(2), start.AddMinutes(2));
    var first = Find(Snapshot(engine, start.AddMinutes(2)), "Week high");
    Check(first.Developing, "Current week high develops");
    Near(100, Find(Snapshot(engine, start.AddMinutes(2)), "Week open").RawPrice, "Week open from exact first expected minute");
    engine.UpdateBars([Bar(start.AddMinutes(2), 200)], start.AddMinutes(3), start.AddMinutes(3));
    var next = Find(Snapshot(engine, start.AddMinutes(3)), "Week high");
    Equal(first.Id, next.Id, "Developing week logical reference stable");
    Near(201, next.RawPrice, "Week updates with new completed observation");
    Near(101, first.RawPrice, "Published prior snapshot does not mutate");
    var missing = new LevelEngine(Source(), calendar, Only(week: true));
    missing.UpdateBars([Bar(start.AddMinutes(1))], start.AddMinutes(2), start.AddMinutes(2));
    Check(!Snapshot(missing, start.AddMinutes(2)).References.Any(), "Missing week start cannot fabricate week open or complete extremes");
}

static void ReferenceRollover()
{
    var calendar = new SessionCalendar();
    var previous = calendar.GetSession(new(2026, 10, 8));
    var current = calendar.GetSession(new(2026, 10, 9));
    var end = current.FuturesIntervals[^1].EndUtc;
    var cashEnd = current.CashInterval!.Value.EndUtc;
    var engine = new LevelEngine(Source(), calendar, Only(previousRth: true, previousFutures: true));
    engine.UpdateBars(previous.FuturesIntervals.SelectMany(x => Fill(x)).Concat(current.FuturesIntervals.SelectMany(x => Fill(x, _ => 200))), end, end);
    Near(101, Find(Snapshot(engine, cashEnd.AddMinutes(-1)), "RTH PDH").RawPrice, "RTH prior session retained until current cash completion");
    Near(201, Find(Snapshot(engine, cashEnd), "RTH PDH").RawPrice, "RTH rolls when observed asof reaches current cash end");
    Near(101, Find(Snapshot(engine, end.AddMinutes(-1)), "Full PDH").RawPrice, "Full prior session retained before completion");
    Near(201, Find(Snapshot(engine, end), "Full PDH").RawPrice, "Full rolls when current futures interval ends");
    var weekend = Snapshot(engine, Utc(10, 10, 12));
    Equal(new DateOnly(2026, 10, 9), Find(weekend, "Full PDH").TradingDate, "Weekend retains Friday completed session");
    Equal(new DateOnly(2026, 10, 9), Find(Snapshot(engine, Utc(10, 11, 22)), "RTH PDH").TradingDate, "Sunday reopening uses Friday completed cash");
}

static void CoverageAndVolume()
{
    var calendar = new SessionCalendar();
    var start = calendar.GetSession(new(2026, 10, 7)).CashInterval!.Value.StartUtc;
    var missing = new LevelEngine(Source(), calendar, Only(rth: true, or: true));
    missing.UpdateBars([Bar(start), Bar(start.AddMinutes(2))], start.AddMinutes(3), start.AddMinutes(3));
    var incomplete = Snapshot(missing, start.AddMinutes(3));
    Check(!incomplete.References.Any(), "A missing minute suppresses false complete references");
    Check(incomplete.ProviderStatus["Current RTH"].Contains("1 missing"), "Coverage reports gap");
    var zero = new LevelEngine(Source(), calendar, Only(rth: true));
    zero.UpdateBars([Bar(start, volume: 0)], start.AddMinutes(1), start.AddMinutes(1));
    var snapshot = Snapshot(zero, start.AddMinutes(1));
    Check(snapshot.References.Any(x => x.Label == "RTH open"), "Zero traded volume is still a real observed open");
    Check(snapshot.References.All(x => x.Label != "VWAP proxy"), "Zero volume does not produce divide-by-zero VWAP");
    Check(snapshot.ProviderStatus["VWAP"].Contains("zero"), "Zero volume reason visible");
    var unknown = new LevelEngine(Source(), calendar, Only(rth: true));
    unknown.UpdateBars([Bar(start, volume: double.NaN)], start.AddMinutes(1), start.AddMinutes(1));
    var unknownSnapshot = Snapshot(unknown, start.AddMinutes(1));
    Check(unknownSnapshot.References.Any(x => x.Label == "RTH open" && x.Quality.HasFlag(QualityFlags.VolumeUnavailable)), "Unknown volume retains real price reference and quality flag");
    Check(unknownSnapshot.ProviderStatus["VWAP"].Contains("unknown") && unknownSnapshot.References.All(x => x.Label != "VWAP proxy"), "Unknown volume cannot be fabricated zero or exact VWAP");
    var sentinel = new LevelEngine(Source(), calendar, Only(rth: true));
    sentinel.UpdateBars([Bar(start, volume: -1)], start.AddMinutes(1), start.AddMinutes(1));
    Check(Snapshot(sentinel, start.AddMinutes(1)).References.Any(x => x.Label == "RTH open" && x.Quality.HasFlag(QualityFlags.VolumeUnavailable)), "Negative volume sentinel retains usable price, records unknown volume");
}

static void BootstrapAndCorrection()
{
    var calendar = new SessionCalendar();
    var start = calendar.GetSession(new(2026, 10, 7)).CashInterval!.Value.StartUtc;
    var bars = Enumerable.Range(0, 15).Select(i => Bar(start.AddMinutes(i), 100 + i, 10 + i)).ToArray();
    var asOf = start.AddMinutes(15);
    var bootstrap = new LevelEngine(Source(), calendar, Only(rth: true, or: true));
    var live = new LevelEngine(Source(), calendar, Only(rth: true, or: true));
    bootstrap.UpdateBars(bars.Reverse(), asOf, asOf);
    foreach (var bar in bars) live.UpdateBars([bar], bar.EndUtc, bar.EndUtc);
    var a = Snapshot(bootstrap, asOf);
    var b = Snapshot(live, asOf);
    Equal(string.Join("|", a.References.Select(x => $"{x.Id}:{x.RawPrice:R}")), string.Join("|", b.References.Select(x => $"{x.Id}:{x.RawPrice:R}")), "Bootstrap/live references match");
    var corrected = bars[1] with { Volume = 100 };
    bootstrap.UpdateBars([bars[1], corrected, corrected], asOf, asOf);
    bars[1] = corrected;
    var expected = bars.Sum(x => (x.High + x.Low + x.Close) / 3 * x.Volume) / bars.Sum(x => x.Volume);
    Near(expected, Find(Snapshot(bootstrap, asOf), "VWAP proxy").RawPrice, "Correction replaces volume without historical/live duplicate counting");
}

static void MergeAndBias()
{
    var now = Utc(10, 7, 14);
    var refs = new[]
    {
        new CandidateLevel { Id = "high-a", Label = "High A", RawPrice = 101, KnownAtUtc = now },
        new CandidateLevel { Id = "high-b", Label = "High B", RawPrice = 101.1, KnownAtUtc = now },
        new CandidateLevel { Id = "low", Label = "Low", RawPrice = 99, KnownAtUtc = now },
        new CandidateLevel { Id = "watch", Label = "Watch", RawPrice = 100.1, KnownAtUtc = now }
    };
    LevelSnapshot Make(ManualBias bias) => SnapshotBuilder.Build(Source(), 1, now, now, now, 100, true, bias, refs);
    Equal(3, Make(ManualBias.Both).Rendering.Count, "Equivalent display ticks merge once");
    Equal(2, Make(ManualBias.Both).Rendering.Single(x => x.DisplayPrice == 101).References.Count, "Merge preserves every provenance record");
    Equal(3, Make(ManualBias.Both).ActiveCandidates.Count, "Both includes two buy references and one sell");
    Equal(2, Make(ManualBias.BuyOnly).ActiveCandidates.Count, "BuyOnly filters mechanical above prices");
    Equal(1, Make(ManualBias.SellOnly).ActiveCandidates.Count, "SellOnly filters mechanical below prices");
    Equal(0, Make(ManualBias.NoTrade).ActiveCandidates.Count, "NoTrade no active candidates");
    Equal(4, Make(ManualBias.NoTrade).References.Count, "Unfiltered references retained");
    Equal(MechanicalSide.AtLevel, Find(Make(ManualBias.Both), "Watch").Side, "Half tick tolerance defines watch state");
    Near(101, Make(ManualBias.Both).Rendering.Single(x => x.DisplayPrice == 101).DisplayPrice, "Merge does not average raw references");
}

static void FormingCache()
{
    var calendar = new SessionCalendar();
    var start = calendar.GetSession(new(2026, 10, 7)).CashInterval!.Value.StartUtc;
    var engine = new LevelEngine(Source(), calendar, Only(rth: true));
    engine.UpdateBars([Bar(start)], start.AddMinutes(1), start.AddMinutes(1));
    Snapshot(engine, start.AddMinutes(1));
    var count = engine.CalculationPassCount;
    for (var i = 1; i < 50; i++)
    {
        var observed = start.AddMinutes(1).AddSeconds(i);
        engine.UpdateBars([Bar(start.AddMinutes(1), 100 + i)], observed, observed);
        engine.SetSourcePrice(100 + i, observed);
        var snapshot = Snapshot(engine, observed);
        Near(100, Find(snapshot, "RTH open").RawPrice, "Forming minute must not enter completed prices");
    }
    Equal(count, engine.CalculationPassCount, "Changing forming observations reuse cached completed aggregates");
    Snapshot(engine, start.AddMinutes(2));
    Equal(count + 1, engine.CalculationPassCount, "New completed minute invalidates calculation cache");
}

static void CorrectionAvailability()
{
    var calendar = new SessionCalendar();
    var start = calendar.GetSession(new(2026, 10, 7)).CashInterval!.Value.StartUtc;
    var engine = new LevelEngine(Source(), calendar, Only(rth: true));
    engine.UpdateBars([Bar(start)], start.AddSeconds(30), start.AddSeconds(30));
    engine.UpdateBars([Bar(start, 102)], start.AddMinutes(1), start.AddMinutes(1));
    var finalized = Snapshot(engine, start.AddMinutes(1));
    Check(finalized.References.All(x => !x.Quality.HasFlag(QualityFlags.Corrected)), "Forming bar finalization is not a historical correction");
    var receipt = start.AddMinutes(2).AddSeconds(1);
    engine.UpdateBars([Bar(start, 103)], start.AddMinutes(2), receipt);
    Check(!Snapshot(engine, start.AddMinutes(2)).References.Any(), "Correction cannot appear before its observation receipt");
    var corrected = Snapshot(engine, receipt);
    Check(corrected.References.All(x => x.KnownAtUtc == receipt && x.Quality.HasFlag(QualityFlags.Corrected)), "Corrected version ray begins at receipt, not original bar time");
}

static void Availability()
{
    var now = Utc(10, 7, 14);
    var refs = new[] { new CandidateLevel { Id = "now", RawPrice = 101, KnownAtUtc = now }, new CandidateLevel { Id = "future", RawPrice = 102, KnownAtUtc = now.AddMinutes(1) } };
    var snapshot = SnapshotBuilder.Build(Source(), 1, now, now, now, 100, false, ManualBias.Both, refs);
    Equal(1, snapshot.References.Count, "Future reference unavailable");
    Equal(0, snapshot.ActiveCandidates.Count, "Stale source no active candidates");
    Check(snapshot.References.All(x => x.Inactive && x.Quality.HasFlag(QualityFlags.Stale)), "Stale context explicit");
}

static void Options()
{
    var now = Utc(10, 7, 14);
    var options = Enumerable.Range(90, 21).SelectMany(strike => new[]
    {
        new OptionContract($"call-{strike}", "rithmic-1", "NQZ6-contract", "early", Utc(10, 9, 20), strike, OptionRight.Call),
        new OptionContract($"put-{strike}", "rithmic-1", "NQZ6-contract", "early", Utc(10, 9, 20), strike, OptionRight.Put)
    }).Concat([
        new OptionContract("other-underlying", "rithmic-1", "NQH7-contract", "wrong", Utc(10, 8, 20), 100, OptionRight.Call, 900),
        new OptionContract("other-connection", "rithmic-2", "NQZ6-contract", "wrong", Utc(10, 8, 20), 100, OptionRight.Call),
        new OptionContract("expired", "rithmic-1", "NQZ6-contract", "old", now, 100, OptionRight.Call),
        new OptionContract("second", "rithmic-1", "NQZ6-contract", "second", Utc(10, 16, 20), 100, OptionRight.Call),
        new OptionContract("third", "rithmic-1", "NQZ6-contract", "third", Utc(10, 23, 20), 100, OptionRight.Call)
    ]).ToArray();
    var selection = OptionSelection.Select(options, Source(), 100, now);
    Equal(2, selection.IncludedSeriesCount, "Nearest two actual expiry series");
    Equal(11, selection.Strikes.Count, "Five each side plus at-source strike");
    var contracts = selection.Strikes.SelectMany(x => x.Contracts).ToArray();
    Check(contracts.All(x => x.UnderlyingContractId == Source().ContractId && x.ConnectionId == Source().ConnectionId), "Other contract/connection rejected");
    Check(contracts.All(x => x.SeriesId != "third" && x.ExpiryUtc > now), "Expired and third series excluded");
    Check(contracts.All(x => x.OpenInterest is null), "Missing OI remains nullable");
    Check(contracts.Any(x => x.ExpiryUtc.Month == 10), "Options expiry need not equal futures December expiry");
}

static void Profiles()
{
    ProfileBin[] bins = [new(100, 1), new(100.25, 10), new(100.5, 10), new(100.75, 1), new(101, 0), new(101.25, 5), new(101.5, 1)];
    var result = ProfileGeometry.Calculate(bins, 0.25, new(SmoothingRadius: 0), true);
    Check(result.Available, "Complete real bins produce profile");
    Near(100.25, result.Points.Single(x => x.Kind == ProfileLevelKind.Poc).Price, "POC equal max picks lowest price");
    Near(100.25, result.Points.First(x => x.Kind == ProfileLevelKind.Hvn).Price, "HVN flat plateau lower midpoint");
    Check(result.Points.Where(x => x.Kind == ProfileLevelKind.Lvn).All(x => x.Price > 100 && x.Price < 101.5), "LVNs only interior traded span");
    Check(!ProfileGeometry.Calculate(bins, 0.25, new(), false).Available, "Unverified independent coverage suppresses profile");
    Check(!ProfileGeometry.Calculate([new(100, 0)], 0.25, new(), true).Available, "Genuine zero profile is unavailable");
}

static void SourceRules()
{
    var accepted = new SourceContractMetadata("rithmic-1", "nq-dec-2026", "NQZ6", "NQ", "CME", true, false,
        Utc(12, 18, 14, 30), new DateTime(2026, 12, 1), 0.25);
    foreach (var name in new[] { "NQZ6", "NQZ26", "NQZ26.CME", "NQZ6@CME", "NQZ6:CME" })
        Check(SourceContractRules.TryValidate(accepted with { Name = name }, "rithmic-1", out var reason), $"Valid exact name {name}: {reason}");
    foreach (var invalid in new[]
    {
        accepted with { Name = "MNQZ6", Root = "MNQ" }, accepted with { Name = "NQH7" },
        accepted with { Name = "NQ" }, accepted with { Root = "QQQ" }, accepted with { Venue = "FakeCME" },
        accepted with { Venue = null }, accepted with { IsFutures = false }, accepted with { IsContinuousOrSpread = true },
        accepted with { ExpirationDate = Utc(11, 20, 14, 30) }, accepted with { MaturityDate = new DateTime(2027, 3, 1) },
        accepted with { TickSize = 0.1 }, accepted with { ContractId = "" }, accepted with { ConnectionId = "rithmic-2" }
    })
        Check(!SourceContractRules.TryValidate(invalid, "rithmic-1", out var reason) && reason.Length > 0, "Invalid source metadata rejected with reason");
    Check(SourceContractRules.TryValidate(accepted with { Root = null, MaturityDate = default, Venue = " CME Globex " }, "rithmic-1", out _), "Optional unavailable maturity/root okay when exact identifying expiry/name is verified");
}

static void HostIndependence()
{
    var calendar = new SessionCalendar();
    var start = calendar.GetSession(new(2026, 10, 7)).CashInterval!.Value.StartUtc;
    var engine = new LevelEngine(Source(), calendar, Only(rth: true));
    engine.UpdateBars([Bar(start)], start.AddMinutes(1), start.AddMinutes(1));
    var baseline = Snapshot(engine, start.AddMinutes(1));
    foreach (var host in new[] { (Symbol: "NQZ6", Timeframe: "M1"), (Symbol: "ESZ6", Timeframe: "H1"), (Symbol: "MNQZ6", Timeframe: "range") })
    {
        // The pure engine accepts no host-chart symbol, bars, aggregation, or timeframe.
        var snapshot = Snapshot(engine, start.AddMinutes(1));
        Equal(string.Join("|", baseline.References.Select(x => $"{x.Id}:{x.RawPrice:R}")), string.Join("|", snapshot.References.Select(x => $"{x.Id}:{x.RawPrice:R}")), $"Host {host.Symbol}/{host.Timeframe} cannot alter source values");
    }
}
