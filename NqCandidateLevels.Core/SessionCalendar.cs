using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace NqCandidateLevels.Core;

/// <summary>A UTC half-open interval: StartUtc is included and EndUtc is excluded.</summary>
public readonly record struct UtcInterval
{
    public DateTime StartUtc { get; }
    public DateTime EndUtc { get; }
    public TimeSpan Duration => EndUtc - StartUtc;

    public UtcInterval(DateTime startUtc, DateTime endUtc)
    {
        if (startUtc.Kind != DateTimeKind.Utc || endUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Session timestamps must have DateTimeKind.Utc.");
        if (endUtc <= startUtc)
            throw new ArgumentException("Session interval end must follow start.");
        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public bool Contains(DateTime utc) => utc >= StartUtc && utc < EndUtc;
    public bool Overlaps(UtcInterval other) => StartUtc < other.EndUtc && other.StartUtc < EndUtc;
    public UtcInterval? Intersect(UtcInterval other)
    {
        var start = StartUtc > other.StartUtc ? StartUtc : other.StartUtc;
        var end = EndUtc < other.EndUtc ? EndUtc : other.EndUtc;
        return end > start ? new UtcInterval(start, end) : null;
    }
}

public sealed record SessionSchedule
{
    public required DateOnly TradingDate { get; init; }
    public required IReadOnlyList<UtcInterval> FuturesIntervals { get; init; }
    public UtcInterval? CashInterval { get; init; }
    public UtcInterval? OvernightInterval { get; init; }
    public required bool IsCertain { get; init; }
    public string? UncertaintyReason { get; init; }
    public required bool IsTradingDay { get; init; }
    public string Source { get; init; } = "Built-in 2026 regular CME/Nasdaq schedule";
    public DateTime? FuturesStartUtc => FuturesIntervals.Count == 0 ? null : FuturesIntervals[0].StartUtc;
    public DateTime? FuturesEndUtc => FuturesIntervals.Count == 0 ? null : FuturesIntervals[^1].EndUtc;
    public bool IsOpen(DateTime utc) => IsCertain && FuturesIntervals.Any(x => x.Contains(utc));
}

/// <summary>
/// NQZ6 calendar with explicit exchange-local schedules converted to UTC.
/// Known special dates are uncertain until an operator supplies a verified override.
/// It does not infer a provider's holiday trade-date assignments from weekday arithmetic.
/// </summary>
public sealed class SessionCalendar
{
    private static readonly TimeZoneInfo Chicago = FindTimeZone("America/Chicago", "Central Standard Time");
    private static readonly TimeZoneInfo NewYork = FindTimeZone("America/New_York", "Eastern Standard Time");
    public static readonly DateOnly NqZ6LastTradingDate = new(2026, 12, 18);
    public static readonly DateTime NqZ6LastTradingUtc = ToUtc(NqZ6LastTradingDate, new TimeOnly(9, 30), NewYork);

    // Conservative affected civil/reference dates from CME's 2026 holiday periods.
    // Adjacent days matter because CME can carry multiple civil sessions in one trade date.
    private static readonly IReadOnlyDictionary<DateOnly, string> SpecialDates = BuildSpecialDates();
    private readonly IReadOnlyDictionary<DateOnly, SessionSchedule> overrides;
    private readonly SessionSchedule[] overrideWindows;

    public string? LoadError { get; }
    public string? OverridePath { get; }
    public string Status => LoadError is not null ? "Calendar unavailable: " + LoadError
        : overrides.Count == 0 ? "2026 regular schedule; holiday periods require verified overrides"
        : $"2026 regular schedule; {overrides.Count} local session overrides loaded";

    public SessionCalendar(string? overridePath = null)
    {
        OverridePath = string.IsNullOrWhiteSpace(overridePath) ? null : overridePath;
        Dictionary<DateOnly, SessionSchedule> loaded = [];
        if (OverridePath is not null)
        {
            try
            {
                OverridePath = Path.GetFullPath(OverridePath);
                var fileInfo = new FileInfo(OverridePath);
                if (!fileInfo.Exists)
                    throw new InvalidDataException("Override file does not exist: " + OverridePath);
                if (fileInfo.Length > 1024 * 1024)
                    throw new InvalidDataException("Calendar override file exceeds 1 MiB.");
                using var stream = fileInfo.OpenRead();
                var document = JsonSerializer.Deserialize<CalendarFile>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                    MaxDepth = 16
                }) ?? throw new InvalidDataException("Calendar override file is empty.");
                if (document.SchemaVersion != 1)
                    throw new InvalidDataException("Calendar schemaVersion must be 1.");
                if (document.Sessions is null || document.Sessions.Count > 400)
                    throw new InvalidDataException("Calendar must contain at most 400 session overrides.");
                foreach (var entry in document.Sessions)
                {
                    if (entry is null)
                        throw new InvalidDataException("Calendar session entry cannot be null.");
                    var schedule = ReadOverride(entry);
                    if (!loaded.TryAdd(schedule.TradingDate, schedule))
                        throw new InvalidDataException($"Duplicate tradingDate {schedule.TradingDate:yyyy-MM-dd}.");
                }
                ValidateCrossDateOverlap(loaded);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                                      or ArgumentException or InvalidOperationException or FormatException or NotSupportedException)
            {
                // A requested calendar may describe closures missing from the default. Falling back
                // to an apparently certain regular schedule would turn that mistake into false levels.
                LoadError = e.Message;
                loaded.Clear();
            }
        }
        overrides = new ReadOnlyDictionary<DateOnly, SessionSchedule>(loaded);
        overrideWindows = loaded.Values.Where(x => x.FuturesIntervals.Count > 0)
            .OrderBy(x => x.FuturesIntervals[0].StartUtc).ToArray();
    }

    public SessionSchedule GetSession(DateOnly tradingDate)
    {
        // No local override can make an expired contract trade again.
        var schedule = tradingDate > NqZ6LastTradingDate ? Closed(tradingDate, "NQZ6 expired")
            : overrides.TryGetValue(tradingDate, out var specified) ? specified : Standard(tradingDate);
        return LoadError is null ? schedule : schedule with { IsCertain = false, UncertaintyReason = LoadError };
    }

    /// <summary>
    /// Exchange reference date, including during a maintenance break. IsOpen decides whether
    /// trading is scheduled. In an ordinary closed weekend this retains the preceding Friday.
    /// Explicit merged holiday trade-date windows take precedence over the normal 17:00 rule.
    /// </summary>
    public DateOnly GetTradingDate(DateTime utc)
    {
        RequireUtc(utc);
        foreach (var schedule in overrideWindows)
            if (utc >= schedule.FuturesStartUtc && utc < schedule.FuturesEndUtc)
                return schedule.TradingDate;
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, Chicago);
        var date = DateOnly.FromDateTime(local);
        if (local.TimeOfDay >= new TimeSpan(17, 0, 0))
            date = date.AddDays(1);
        if (date.DayOfWeek == DayOfWeek.Saturday)
            date = date.AddDays(-1);
        else if (date.DayOfWeek == DayOfWeek.Sunday)
            date = date.AddDays(-2);
        return date;
    }

    public DateOnly GetWeekStart(DateOnly tradingDate)
    {
        var daysFromMonday = ((int)tradingDate.DayOfWeek + 6) % 7;
        return tradingDate.AddDays(-daysFromMonday);
    }

    public DateOnly PreviousTradingDate(DateOnly tradingDate)
    {
        for (var candidate = tradingDate.AddDays(-1); ; candidate = candidate.AddDays(-1))
            if (GetSession(candidate).IsTradingDay)
                return candidate;
    }

    /// <summary>Cash references skip explicit cash closures, retaining the last applicable RTH.</summary>
    public DateOnly PreviousCashTradingDate(DateOnly tradingDate)
    {
        for (var candidate = tradingDate.AddDays(-1); ; candidate = candidate.AddDays(-1))
        {
            var schedule = GetSession(candidate);
            if (schedule.IsTradingDay && schedule.CashInterval is not null)
                return candidate;
        }
    }

    public bool IsOpen(DateTime utc)
    {
        RequireUtc(utc);
        return utc < NqZ6LastTradingUtc && GetSession(GetTradingDate(utc)).IsOpen(utc);
    }

    public static DateTime ChicagoToUtc(DateOnly date, TimeOnly time) => ToUtc(date, time, Chicago);
    public static DateTime NewYorkToUtc(DateOnly date, TimeOnly time) => ToUtc(date, time, NewYork);

    private static SessionSchedule Standard(DateOnly date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || date > NqZ6LastTradingDate)
            return Closed(date, "Weekend/expired contract");
        var start = ToUtc(date.AddDays(-1), new TimeOnly(17, 0), Chicago);
        var scheduledEnd = ToUtc(date, new TimeOnly(16, 0), Chicago);
        var end = scheduledEnd < NqZ6LastTradingUtc ? scheduledEnd : NqZ6LastTradingUtc;
        var cashStart = ToUtc(date, new TimeOnly(9, 30), NewYork);
        var cashEnd = ToUtc(date, new TimeOnly(16, 0), NewYork);
        var reason = date.Year != 2026 ? "Built-in calendar is verified only for ordinary 2026 weekdays."
            : SpecialDates.TryGetValue(date, out var special) ? special : null;
        var cashClosed = IsCashHoliday2026(date) || date == NqZ6LastTradingDate;
        if (date == new DateOnly(2026, 11, 27) || date == new DateOnly(2026, 12, 24))
            cashEnd = ToUtc(date, new TimeOnly(13, 0), NewYork);
        return new SessionSchedule
        {
            TradingDate = date,
            FuturesIntervals = Array.AsReadOnly(new[] { new UtcInterval(start, end) }),
            CashInterval = cashClosed ? null : new UtcInterval(cashStart, cashEnd),
            // NQZ6's last overnight ends exactly at its termination, even though it has no RTH.
            OvernightInterval = !cashClosed || date == NqZ6LastTradingDate ? new UtcInterval(start, cashStart) : null,
            IsCertain = reason is null,
            UncertaintyReason = reason,
            IsTradingDay = true,
            Source = date == NqZ6LastTradingDate ? "CME rule 35902.G/35903.A: NQZ6 termination" : "Built-in 2026 regular CME/Nasdaq schedule"
        };
    }

    private static SessionSchedule Closed(DateOnly date, string source) => new()
    {
        TradingDate = date,
        FuturesIntervals = Array.Empty<UtcInterval>(),
        IsCertain = true,
        IsTradingDay = false,
        Source = source
    };

    private static SessionSchedule ReadOverride(CalendarEntry entry)
    {
        if (!DateOnly.TryParseExact(entry.TradingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            throw new InvalidDataException("tradingDate must be yyyy-MM-dd.");
        if (date.Year != 2026)
            throw new InvalidDataException("NQZ6 calendar overrides must identify a 2026 reference date.");
        if (entry.Verified && string.IsNullOrWhiteSpace(entry.Source))
            throw new InvalidDataException($"Verified {date:yyyy-MM-dd} override requires its authoritative source.");
        if (entry.FuturesClosed is null || entry.CashClosed is null)
            throw new InvalidDataException($"{date:yyyy-MM-dd} must explicitly set futuresClosed and cashClosed.");
        var localIntervals = entry.FuturesIntervalsChicago ?? [];
        if (localIntervals.Any(x => x is null))
            throw new InvalidDataException($"{date:yyyy-MM-dd}: futures interval cannot be null.");
        if (entry.FuturesClosed.Value == (localIntervals.Count > 0))
            throw new InvalidDataException($"{date:yyyy-MM-dd}: futuresClosed must agree with futuresIntervalsChicago.");
        if (localIntervals.Count > 32)
            throw new InvalidDataException($"{date:yyyy-MM-dd}: at most 32 futures intervals are supported.");
        if (entry.CashClosed.Value == (entry.CashNewYork is not null))
            throw new InvalidDataException($"{date:yyyy-MM-dd}: cashClosed must agree with cashNewYork.");
        var futures = localIntervals.Select(x => ReadLocalInterval(x, Chicago)).OrderBy(x => x.StartUtc).ToList();
        var earliestAllowed = ToUtc(date.AddDays(-7), new TimeOnly(0, 0), Chicago);
        var latestAllowed = ToUtc(date.AddDays(1), new TimeOnly(0, 0), Chicago);
        for (var i = 0; i < futures.Count; i++)
        {
            if (futures[i].StartUtc < earliestAllowed || futures[i].EndUtc > latestAllowed)
                throw new InvalidDataException($"{date:yyyy-MM-dd}: futures intervals must end on their reference date and span at most seven preceding dates.");
            if (i > 0 && futures[i - 1].EndUtc > futures[i].StartUtc)
                throw new InvalidDataException($"{date:yyyy-MM-dd}: futures intervals overlap.");
        }
        UtcInterval? cash = entry.CashNewYork is null ? null : ReadLocalInterval(entry.CashNewYork, NewYork);
        if (cash is { } cashWindow && (DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(cashWindow.StartUtc, NewYork)) != date
            || DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(cashWindow.EndUtc.AddTicks(-1), NewYork)) != date))
            throw new InvalidDataException($"{date:yyyy-MM-dd}: cash window must be on its New York reference date.");
        // A calendar is a schedule for this contract, not permission to bypass expiration.
        futures = futures.Where(x => x.StartUtc < NqZ6LastTradingUtc)
            .Select(x => x.EndUtc <= NqZ6LastTradingUtc ? x : new UtcInterval(x.StartUtc, NqZ6LastTradingUtc)).ToList();
        if (date >= NqZ6LastTradingDate)
            cash = null;
        if (cash is { } requiredCash && (futures.Count == 0 || requiredCash.StartUtc < futures[0].StartUtc
            || requiredCash.EndUtc > futures[^1].EndUtc))
            throw new InvalidDataException($"{date:yyyy-MM-dd}: cash window must be within the source futures session bounds.");
        return new SessionSchedule
        {
            TradingDate = date,
            FuturesIntervals = futures.AsReadOnly(),
            CashInterval = cash,
            OvernightInterval = cash is { } c && futures.Count > 0 && c.StartUtc > futures[0].StartUtc
                ? new UtcInterval(futures[0].StartUtc, c.StartUtc)
                : date == NqZ6LastTradingDate && futures.Count > 0 ? new UtcInterval(futures[0].StartUtc, NqZ6LastTradingUtc) : null,
            IsCertain = entry.Verified,
            IsTradingDay = futures.Count > 0,
            UncertaintyReason = entry.Verified ? null : entry.Reason ?? "Local session override is not verified.",
            Source = entry.Source ?? "Local unverified override"
        };
    }

    private static void ValidateCrossDateOverlap(Dictionary<DateOnly, SessionSchedule> loaded)
    {
        foreach (var pair in loaded)
        {
            for (var offset = -7; offset <= 1; offset++)
            {
                if (offset == 0)
                    continue;
                var neighborDate = pair.Key.AddDays(offset);
                var neighbor = loaded.TryGetValue(neighborDate, out var supplied) ? supplied : Standard(neighborDate);
                if (pair.Value.FuturesIntervals.Any(a => neighbor.FuturesIntervals.Any(a.Overlaps)))
                    throw new InvalidDataException($"Calendar {pair.Key:yyyy-MM-dd} overlaps {neighborDate:yyyy-MM-dd}. Explicitly close/remap all dates in a merged holiday trade date.");
            }
        }
    }

    private static UtcInterval ReadLocalInterval(LocalInterval interval, TimeZoneInfo zone)
        => new(ParseLocalUtc(interval.Start, zone), ParseLocalUtc(interval.End, zone));

    private static DateTime ParseLocalUtc(string? value, TimeZoneInfo zone)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            throw new InvalidDataException("Local session timestamps must be yyyy-MM-ddTHH:mm:ss without an offset.");
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new InvalidDataException($"Ambiguous/nonexistent exchange-local timestamp {value} in {zone.Id}.");
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static DateTime ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new ArgumentException("Ambiguous/nonexistent exchange-local timestamp.");
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static TimeZoneInfo FindTimeZone(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    private static void RequireUtc(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Calendar inputs must have DateTimeKind.Utc.", nameof(utc));
    }

    private static IReadOnlyDictionary<DateOnly, string> BuildSpecialDates()
    {
        var dates = new Dictionary<DateOnly, string>();
        void Add(string reason, params string[] isoDates)
        {
            foreach (var date in isoDates)
                dates[DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)] = reason + "; verified NQ Globex holiday trade-date override required.";
        }
        Add("New Year holiday period", "2026-01-01", "2026-01-02");
        Add("Martin Luther King holiday period", "2026-01-19", "2026-01-20");
        Add("Presidents Day holiday period", "2026-02-16", "2026-02-17");
        Add("Good Friday holiday period", "2026-04-02", "2026-04-03", "2026-04-06");
        Add("Memorial Day holiday period", "2026-05-25", "2026-05-26");
        Add("Juneteenth holiday period", "2026-06-18", "2026-06-19", "2026-06-22");
        Add("Independence Day holiday period", "2026-07-02", "2026-07-03", "2026-07-06");
        Add("Labor Day holiday period", "2026-09-07", "2026-09-08");
        Add("Thanksgiving holiday period", "2026-11-25", "2026-11-26", "2026-11-27");
        Add("Christmas holiday period", "2026-12-24", "2026-12-25", "2026-12-28");
        Add("New Year holiday period", "2026-12-31");
        return new ReadOnlyDictionary<DateOnly, string>(dates);
    }

    private static bool IsCashHoliday2026(DateOnly date) => date.Year == 2026 &&
        ((date.Month, date.Day) is (1, 1) or (1, 19) or (2, 16) or (4, 3) or (5, 25)
            or (6, 19) or (7, 3) or (9, 7) or (11, 26) or (12, 25));

    private sealed class CalendarFile
    {
        public int SchemaVersion { get; set; }
        public string? Description { get; set; }
        public List<CalendarEntry>? Sessions { get; set; }
    }
    private sealed class CalendarEntry
    {
        public string? TradingDate { get; set; }
        public bool Verified { get; set; }
        public string? Source { get; set; }
        public string? Reason { get; set; }
        public bool? FuturesClosed { get; set; }
        public List<LocalInterval>? FuturesIntervalsChicago { get; set; }
        public bool? CashClosed { get; set; }
        public LocalInterval? CashNewYork { get; set; }
    }
    private sealed class LocalInterval
    {
        public string? Start { get; set; }
        public string? End { get; set; }
    }
}
