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
    [InputParameter("Option series scan budget", 71, 1, 8, 1, 0)] public int OptionSeries = 8;
    [InputParameter("Selected concentration strikes per side", 72, 1, 50, 1, 0)] public int StrikesEachSide = 3;
    [InputParameter("Options metadata refresh (seconds)", 73, 30, 1800, 30, 0)] public int OptionRefreshSeconds = 120;
    [InputParameter("Options cache TTL (seconds)", 74, 30, 3600, 30, 0)] public int OptionCacheTtlSeconds = 300;
    [InputParameter("Options minimum band half-width (points)", 75, 1, 2000, 25, 0)] public int OptionBandHalfWidth = 500;
    [InputParameter("Options raw concentration threshold", 76, .01, 1, .01, 2)] public double OptionThreshold = .65;
    [InputParameter("Options minimum valid buckets", 77, 3, 1000, 1, 0)] public int OptionMinimumBuckets = 10;
    [InputParameter("Options minimum returned-bucket coverage", 78, .01, 1, .01, 2)] public double OptionMinimumCoverage = .70;
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
    [InputParameter("Importance zone width (NQ ticks)", 100, 0, 40, 1, 0)] public int ZoneToleranceTicks = 8;
    [InputParameter("Minimum displayed importance", 101, 0, 100, 1, 0)] public double MinimumScore = 0;
    [InputParameter("Minimum displayed tier", 102)] public ImportanceTier MinimumTier = ImportanceTier.D;
    [InputParameter("Maximum displayed zones per side", 103, 1, 30, 1, 0)] public int ZonesEachSide = 6;
    [InputParameter("Maximum watch zones", 104, 0, 10, 1, 0)] public int WatchZones = 3;
    [InputParameter("Show score components", 105)] public bool ComponentDetails = false;
    [InputParameter("Subtle zone fill", 106)] public bool FillZones = true;
    [InputParameter("Base component maximum", 110, 0, 100, 1, 0)] public double BaseWeight = 40;
    [InputParameter("Confluence component maximum", 111, 0, 100, 1, 0)] public double ConfluenceWeight = 25;
    [InputParameter("Options component maximum", 112, 0, 100, 1, 0)] public double OptionsWeight = 25;
    [InputParameter("Profile component maximum", 113, 0, 100, 1, 0)] public double ProfileWeight = 10;
    [InputParameter("Critical A threshold", 114, 0, 100, 1, 0)] public double CriticalThreshold = 75;
    [InputParameter("Major B threshold", 115, 0, 100, 1, 0)] public double MajorThreshold = 50;
    [InputParameter("Secondary C threshold", 116, 0, 100, 1, 0)] public double SecondaryThreshold = 35;

    private SourceDataAdapter? adapter;
    private AdapterView view = new(null, "initializing NQZ6", "options pending", "volume profile disabled", "", "history pending");
    private long generation;
    private long lastRepaintTicks;
    private AdapterSettings? activeSettings;

    /// <summary>Immutable source snapshot for a future scorer; includes unfiltered References and separate ActiveCandidates.</summary>
    public LevelSnapshot? CurrentSnapshot => Volatile.Read(ref view).Snapshot;
    public ScoredLevelSnapshot? CurrentScoredSnapshot => Volatile.Read(ref view).ScoredSnapshot;

    public NqZ6CandidateLevelsIndicator()
    {
        Name = "NQZ6 Candidate Levels";
        Description = "Independent NQZ6 references with deterministic importance-v1 ranking and manual permissions; no orders.";
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
                    3 => "Price level families", 4 => "Opening range", 5 => "Confirmed structure", 6 => "True volume profile", 7 => "NQZ6 options", 8 => "Rendering", 10 => "Reference importance", 11 => "Importance hypotheses / weights", _ => "Diagnostics" };
                if (!groups.TryGetValue(bucket, out var group)) groups[bucket] = group = new(text, bucket);
                item.SeparatorGroup = group;
            }
            return result;
        }
        set
        {
            // Keep saved numerical budgets: the old strike count remains a display count.
            // Renaming the SDK setting keys explicitly migrates old workspaces.
            var migrated = value.Select(item => item.GetCopy()).ToList();
            foreach (var item in migrated)
            {
                if (item.Name == "Nearest unexpired option series") item.Name = "Option series scan budget";
                if (item.Name == "Distinct strikes on each side") item.Name = "Selected concentration strikes per side";
            }
            base.Settings = migrated;
        }
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
            Math.Clamp(ProfileNodeLimit, 1, 10), Math.Clamp(StaleSeconds, 15, 600))
        {
            Scoring = new ScoringSettings { ZoneToleranceTicks = Math.Clamp(ZoneToleranceTicks, 0, 40),
                AtLevelToleranceTicks = Math.Clamp(AtLevelToleranceTicks, 0, 2),
                BaseMaximum = Math.Clamp(BaseWeight, 0, 100), ConfluenceMaximum = Math.Clamp(ConfluenceWeight, 0, 100),
                OptionsMaximum = Math.Clamp(OptionsWeight, 0, 100), ProfileMaximum = Math.Clamp(ProfileWeight, 0, 100),
                SecondaryThreshold = Math.Clamp(SecondaryThreshold, 0, 100),
                MajorThreshold = Math.Clamp(MajorThreshold, Math.Clamp(SecondaryThreshold, 0, 100), 100),
                CriticalThreshold = Math.Clamp(CriticalThreshold, Math.Max(Math.Clamp(SecondaryThreshold, 0, 100), Math.Clamp(MajorThreshold, 0, 100)), 100),
                OptionRetrievalTtlSeconds = Math.Clamp(OptionCacheTtlSeconds, 30, 3600) },
            OptionConcentration = new OptionConcentrationSettings
            {
                SelectedStrikesEachSide = Math.Clamp(StrikesEachSide, 1, 50),
                CacheTtlSeconds = Math.Clamp(OptionCacheTtlSeconds, 30, 3600),
                MinimumBandHalfWidth = Math.Clamp(OptionBandHalfWidth, 1, 2000),
                RawConcentrationThreshold = Math.Clamp(OptionThreshold, .01, 1),
                MinimumValidBuckets = Math.Clamp(OptionMinimumBuckets, 3, 1000),
                MinimumCoverage = Math.Clamp(OptionMinimumCoverage, .01, 1)
            }
        };
        if (adapter != null && activeSettings != null && requested with { Bias = activeSettings.Bias,
            Scoring = activeSettings.Scoring, OptionConcentration = activeSettings.OptionConcentration,
            StrikesEachSide = activeSettings.StrikesEachSide } == activeSettings)
        {
            adapter.UpdateScoringAndBias(requested.Scoring, Bias, requested.OptionConcentration);
            activeSettings = requested;
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
                ShowContext, ShowLabels, Math.Clamp(TableRows, 5, 100), DetailedHealth, Bias,
                Math.Clamp(MinimumScore, 0, 100), MinimumTier, Math.Clamp(ZonesEachSide, 1, 30),
                Math.Clamp(WatchZones, 0, 10), ComponentDetails, FillZones));
    }
}
