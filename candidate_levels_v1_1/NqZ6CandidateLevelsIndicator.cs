using System.Drawing;
using NqCandidateLevels.Core;
using TradingPlatform.BusinessLayer;

namespace NqCandidateLevels;

public sealed class NqZ6CandidateLevelsIndicator : Indicator
{
    [InputParameter("Manual bias", 0)] public ManualBias Bias = ManualBias.Both;
    [InputParameter("Rithmic connection ID (blank = exactly one active)", 10)] public string RithmicConnectionId = "";
    [InputParameter("Exact NQZ6 contract ID (optional lookup)", 11)] public string SourceContractId = "";
    [InputParameter("History lookback (calendar days)", 12, 7, 90, 1, 0)] public int LookbackDays = 30;
    [InputParameter("Stale open-market feed (seconds)", 13, 15, 600, 5, 0)] public int StaleSeconds = 90;
    [InputParameter("Verified calendar override JSON path", 20)] public string CalendarPath = "";
    [InputParameter("Previous RTH references", 30)] public bool PreviousRth = true;
    [InputParameter("Previous futures-session references", 31)] public bool PreviousFutures = true;
    [InputParameter("Overnight high/low", 32)] public bool Overnight = true;
    [InputParameter("Current week high/low/open", 33)] public bool CurrentWeek = true;
    [InputParameter("Previous week high/low", 34)] public bool PreviousWeek = true;
    [InputParameter("Current RTH open and VWAP proxy", 35)] public bool CurrentRth = true;
    [InputParameter("Opening range", 36)] public bool OpeningRange = true;
    [InputParameter("OR duration (minutes)", 40, 1, 120, 1, 0)] public int OpeningRangeMinutes = 15;
    [InputParameter("Show developing opening range", 41)] public bool DevelopingOpeningRange = false;
    [InputParameter("Confirmed swings", 50)] public bool Structure = true;
    [InputParameter("Swing left completed bars", 51, 1, 20, 1, 0)] public int SwingLeftBars = 2;
    [InputParameter("Swing right completed bars", 52, 1, 20, 1, 0)] public int SwingRightBars = 2;
    [InputParameter("Swing lookback completed bars", 53, 10, 10000, 10, 0)] public int SwingLookbackBars = 1440;
    [InputParameter("Maximum swings per side", 54, 1, 30, 1, 0)] public int SwingCountPerSide = 5;
    [InputParameter("Previous RTH real volume profile", 60)] public bool VolumeProfile = false;
    [InputParameter("Profile bin (ticks)", 61, 1, 40, 1, 0)] public int ProfileBinTicks = 1;
    [InputParameter("Profile local neighborhood (bins)", 62, 1, 20, 1, 0)] public int ProfileNeighborhood = 1;
    [InputParameter("Maximum HVNs and LVNs each", 63, 1, 10, 1, 0)] public int ProfileNodeLimit = 3;
    [InputParameter("Verified NQZ6 option strikes", 70)] public bool OptionStrikes = true;
    [InputParameter("Nearest unexpired option series", 71, 1, 8, 1, 0)] public int OptionSeries = 2;
    [InputParameter("Distinct strikes on each side", 72, 1, 50, 1, 0)] public int StrikesEachSide = 5;
    [InputParameter("Options metadata refresh (seconds)", 73, 30, 1800, 30, 0)] public int OptionRefreshSeconds = 120;
    [InputParameter("At-level tolerance (NQ ticks)", 80, 0, 2, .25, 2)] public double AtLevelToleranceTicks = .5;
    [InputParameter("Eligible buy color", 81)] public Color BuyColor = Color.LimeGreen;
    [InputParameter("Eligible sell color", 82)] public Color SellColor = Color.OrangeRed;
    [InputParameter("Inactive/context color", 83)] public Color ContextColor = Color.Gray;
    [InputParameter("Ray width", 84, 1, 4, 1, 0)] public int RayWidth = 1;
    [InputParameter("Label font size", 85, 7, 16, 1, 0)] public int FontSize = 9;
    [InputParameter("Show inactive reference context", 86)] public bool ShowContext = true;
    [InputParameter("Right-edge labels", 87)] public bool ShowLabels = true;
    [InputParameter("Level table rows", 88, 5, 100, 1, 0)] public int TableRows = 24;
    [InputParameter("Detailed provider health", 90)] public bool DetailedHealth = true;

    private SourceDataAdapter? adapter;
    private AdapterView view = new(null, "initializing NQZ6", "options pending", "volume profile disabled", "", "history pending");
    private long generation;
    private long lastRepaintTicks;
    private AdapterSettings? activeSettings;

    /// <summary>Immutable source snapshot for a future scorer; includes unfiltered References and separate ActiveCandidates.</summary>
    public LevelSnapshot? CurrentSnapshot => Volatile.Read(ref view).Snapshot;

    public NqZ6CandidateLevelsIndicator()
    {
        Name = "NQZ6 Candidate Levels";
        Description = "Independent Rithmic NQZ6 candidate references, mechanical geometry and manual permissions; no orders or scoring.";
        SeparateWindow = false;
        AllowFitAuto = false;
    }

    public override IList<SettingItem> Settings
    {
        get
        {
            var result = base.Settings;
            var groups = new Dictionary<int, SettingItemSeparatorGroup>();
            foreach (var item in result)
            {
                var bucket = item.SortIndex / 10;
                var text = bucket switch { 0 => "Manual permissions", 1 => "Independent Rithmic source", 2 => "Session calendar (exchange time zones)",
                    3 => "Price level families", 4 => "Opening range", 5 => "Confirmed structure", 6 => "True volume profile", 7 => "NQZ6 options", 8 => "Rendering", _ => "Diagnostics" };
                if (!groups.TryGetValue(bucket, out var group)) groups[bucket] = group = new(text, bucket);
                item.SeparatorGroup = group;
            }
            return result;
        }
        set => base.Settings = value;
    }

    protected override void OnInit() => Restart();
    protected override void OnSettingsUpdated() { base.OnSettingsUpdated(); Restart(); }
    protected override void OnUpdate(UpdateArgs args) { /* Source events and the owned timer drive calculations. */ }

    private void Restart()
    {
        var engineSettings = new EngineSettings
        {
            LookbackDays = Math.Clamp(LookbackDays, 7, 90), PreviousRth = PreviousRth, PreviousFutures = PreviousFutures,
            Overnight = Overnight, CurrentWeek = CurrentWeek, PreviousWeek = PreviousWeek, CurrentRth = CurrentRth,
            OpeningRange = OpeningRange, OpeningRangeMinutes = Math.Clamp(OpeningRangeMinutes, 1, 120),
            ShowDevelopingOpeningRange = DevelopingOpeningRange, Structure = Structure,
            SwingLeftBars = Math.Clamp(SwingLeftBars, 1, 20), SwingRightBars = Math.Clamp(SwingRightBars, 1, 20),
            SwingLookbackBars = Math.Clamp(SwingLookbackBars, 10, 10000), SwingCountPerSide = Math.Clamp(SwingCountPerSide, 1, 30),
            AtLevelToleranceTicks = Math.Clamp(AtLevelToleranceTicks, 0, 2)
        };
        var requested = new AdapterSettings(RithmicConnectionId, SourceContractId, CalendarPath, engineSettings, Bias, OptionStrikes,
            Math.Clamp(OptionSeries, 1, 8), Math.Clamp(StrikesEachSide, 1, 50), Math.Clamp(OptionRefreshSeconds, 30, 1800),
            VolumeProfile, Math.Clamp(ProfileBinTicks, 1, 40), Math.Clamp(ProfileNeighborhood, 1, 20),
            Math.Clamp(ProfileNodeLimit, 1, 10), Math.Clamp(StaleSeconds, 15, 600));
        if (adapter != null && activeSettings != null && requested with { Bias = activeSettings.Bias } == activeSettings)
        {
            adapter.SetBias(Bias);
            try { CurrentChart?.RedrawBuffer(); } catch (InvalidOperationException) { }
            return;
        }
        var current = Interlocked.Increment(ref generation);
        Interlocked.Exchange(ref adapter, null)?.Dispose();
        activeSettings = requested;
        Volatile.Write(ref view, new(null, "loading independent NQZ6", "options pending", VolumeProfile ? "profile pending" : "volume profile disabled", RithmicConnectionId, "history pending"));
        adapter = new(requested, snapshot =>
        {
            if (current != Interlocked.Read(ref generation)) return;
            Volatile.Write(ref view, snapshot);
            var now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref lastRepaintTicks) < TimeSpan.TicksPerMillisecond * 250) return;
            Interlocked.Exchange(ref lastRepaintTicks, now);
            try { CurrentChart?.RedrawBuffer(); } catch (InvalidOperationException) { }
        });
    }

    protected override void OnClear()
    {
        Interlocked.Increment(ref generation);
        Interlocked.Exchange(ref adapter, null)?.Dispose();
        activeSettings = null;
        Volatile.Write(ref view, new(null, "removed", "", "", "", ""));
        base.OnClear();
    }

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        base.OnPaintChart(args);
        CandidateRenderer.Paint(args, CurrentChart, Symbol?.Id, Symbol?.ConnectionId, Volatile.Read(ref view),
            new(BuyColor, SellColor, ContextColor, Math.Clamp(RayWidth, 1, 4), Math.Clamp(FontSize, 7, 16),
                ShowContext, ShowLabels, Math.Clamp(TableRows, 5, 100), DetailedHealth, Bias));
    }
}
