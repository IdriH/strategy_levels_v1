using System.Drawing;
using System.Drawing.Drawing2D;
using NqCandidateLevels.Core;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace NqCandidateLevels;

internal sealed record RenderSettings(Color Buy, Color Sell, Color Context, int Width, int FontSize,
    bool ShowContext, bool Labels, int TableRows, bool DetailedHealth, ManualBias Bias,
    double MinimumScore, ImportanceTier MinimumTier, int ZonesEachSide, int WatchZones,
    bool ComponentDetails, bool FillZones);

/// <summary>Only immutable published views and native chart coordinates are read here. No scoring or data requests.</summary>
internal static class CandidateRenderer
{
    public static void Paint(PaintChartEventArgs args, IChart? chart, string? hostId, string? hostConnection,
        AdapterView view, RenderSettings style)
    {
        var window = chart?.MainWindow;
        if (window != null && args.WindowIndex != window.WindowNumber) return;
        var rect = window == null ? args.Rectangle : Rectangle.Intersect(args.Rectangle, window.ClientRectangle);
        if (rect.Width < 30 || rect.Height < 30) return;
        var g = args.Graphics;
        var saved = g.Save();
        try
        {
            g.SetClip(rect, CombineMode.Intersect);
            using var font = new Font("Segoe UI", style.FontSize, FontStyle.Regular, GraphicsUnit.Point);
            using var textBrush = new SolidBrush(Color.Gainsboro);
            using var panelBrush = new SolidBrush(Color.FromArgb(225, 20, 24, 31));
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            var lineHeight = (int)Math.Ceiling(font.GetHeight(g)) + 3;
            var snapshot = view.Snapshot;
            // Never mix a score with a separately published raw version.
            var scored = view.ScoredSnapshot is { } candidate && snapshot?.Version == candidate.SourceSnapshotVersion
                ? candidate : null;
            var mismatched = snapshot != null && (snapshot.Source.ContractId != hostId || snapshot.Source.ConnectionId != hostConnection);
            var rightTime = window?.CoordinatesConverter.GetTime(rect.Right) ?? DateTime.MaxValue;
            var buy = DisplayZones(scored?.ActiveBuyZones, rightTime, style, style.ZonesEachSide);
            var sell = DisplayZones(scored?.ActiveSellZones, rightTime, style, style.ZonesEachSide);
            var watch = style.ShowContext ? DisplayZones(scored?.WatchZones, rightTime, style, style.WatchZones) : [];
            var lines = BuildPanel(view, snapshot, scored, style, mismatched, rightTime, buy, sell, watch);
            var availableRows = Math.Max(1, (rect.Height - 12) / lineHeight);
            var shown = lines.Take(availableRows).ToList();
            if (shown.Count < lines.Count && shown.Count > 0)
                shown[^1] = $"Panel clipped ({shown.Count}/{lines.Count} rows); enlarge chart or reduce font/provider details";
            var panelWidth = Math.Min(rect.Width - 12, 890);
            var panel = new Rectangle(rect.Left + 6, rect.Top + 6, panelWidth, shown.Count * lineHeight + 8);
            var occupied = new List<RectangleF> { panel };

            if (snapshot != null && window != null)
            {
                var converter = window.CoordinatesConverter;
                // Process the strongest labels first, regardless of vertical price order.
                foreach (var zone in buy.Concat(sell).Concat(watch).OrderByDescending(z => z.Score.Total).ThenBy(z => z.Id, StringComparer.Ordinal))
                {
                    var y = converter.GetChartY(zone.AnchorPrice);
                    if (!double.IsFinite(y) || y < rect.Top || y >= rect.Bottom) continue;
                    var rawX = converter.GetChartX(zone.KnownAtUtc);
                    if (!double.IsFinite(rawX) || rawX >= rect.Right) continue;
                    var x = (float)Math.Clamp(rawX, rect.Left, rect.Right);
                    var active = !zone.Inactive && zone.ManualPermission && zone.Side != MechanicalSide.AtLevel;
                    var color = active ? zone.Side == MechanicalSide.BreakoutBuy ? style.Buy : style.Sell : style.Context;
                    color = Color.FromArgb(active ? 235 : 135, color);
                    var width = Math.Max(style.Width, TierWidth(zone.Tier));
                    using var pen = new Pen(color, width) { DashStyle = zone.Developing ? DashStyle.Dash : DashStyle.Solid };
                    var lowY = converter.GetChartY(zone.LowPrice);
                    var highY = converter.GetChartY(zone.HighPrice);
                    if (style.FillZones && zone.HighPrice > zone.LowPrice && double.IsFinite(lowY) && double.IsFinite(highY))
                    {
                        var top = (float)Math.Clamp(Math.Min(lowY, highY), rect.Top, rect.Bottom);
                        var bottom = (float)Math.Clamp(Math.Max(lowY, highY), rect.Top, rect.Bottom);
                        using var bandBrush = new SolidBrush(Color.FromArgb(active ? 24 : 12, color));
                        g.FillRectangle(bandBrush, x, top, rect.Right - x, Math.Max(1, bottom - top));
                        using var bounds = new Pen(Color.FromArgb(active ? 100 : 65, color), 1);
                        g.DrawLine(bounds, x, top, rect.Right, top);
                        g.DrawLine(bounds, x, bottom, rect.Right, bottom);
                    }
                    g.DrawLine(pen, x, (float)y, rect.Right, (float)y);
                    if (style.Labels)
                        DrawLabel($"{zone.Tier} {zone.Score.Total:0} | {State(zone)} | {Reasons(zone)} | {Band(zone)}{Badges(zone)}",
                            (float)y, color, pen);
                }

                // Current strength is never projected into an earlier viewport. Raw references
                // retain their own availability gate and ordinary one-pixel styling.
                var fallbackIds = scored?.Zones.Where(z => z.KnownAtUtc > rightTime)
                    .SelectMany(z => z.Members).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var level in snapshot.Rendering)
                {
                    var references = level.References.Where(r => r.KnownAtUtc <= rightTime &&
                        (scored == null || fallbackIds!.Contains(r.Id))).ToArray();
                    if (references.Length == 0) continue;
                    var activeBuy = references.Any(r => !r.Inactive && r.ManualPermission && r.Side == MechanicalSide.BreakoutBuy);
                    var activeSell = references.Any(r => !r.Inactive && r.ManualPermission && r.Side == MechanicalSide.BreakdownSell);
                    if (!style.ShowContext && !activeBuy && !activeSell) continue;
                    var y = converter.GetChartY(level.DisplayPrice);
                    var rawX = converter.GetChartX(references.Min(r => r.KnownAtUtc));
                    if (!double.IsFinite(y) || y < rect.Top || y >= rect.Bottom || !double.IsFinite(rawX) || rawX >= rect.Right) continue;
                    var color = Color.FromArgb(125, activeBuy ? style.Buy : activeSell ? style.Sell : style.Context);
                    using var pen = new Pen(color, 1) { DashStyle = references.Any(r => r.Developing) ? DashStyle.Dash : DashStyle.Solid };
                    g.DrawLine(pen, (float)Math.Clamp(rawX, rect.Left, rect.Right), (float)y, rect.Right, (float)y);
                    if (style.Labels)
                        DrawLabel($"{string.Join(" / ", references.Select(r => r.Label).Distinct())} {level.DisplayPrice:F2} | strength unavailable at viewport",
                            (float)y, color, pen);
                }
            }

            g.FillRectangle(panelBrush, panel);
            for (var i = 0; i < shown.Count; i++)
                g.DrawString(shown[i], font, textBrush,
                    new RectangleF(panel.Left + 4, panel.Top + 4 + i * lineHeight, panel.Width - 8, lineHeight), format);

            void DrawLabel(string label, float priceY, Color color, Pen pen)
            {
                var width = Math.Min(rect.Width * .60f, g.MeasureString(label, font).Width + 8);
                // Reserve the table and already-placed stronger labels. Bounds stay within the chart.
                for (var step = 0; step <= rect.Height / lineHeight; step++)
                {
                    var offset = step == 0 ? 0 : ((step + 1) / 2) * lineHeight * (step % 2 == 1 ? -1 : 1);
                    var top = priceY - lineHeight / 2f + offset;
                    var labelRect = new RectangleF(rect.Right - width, top, width, lineHeight);
                    if (top < rect.Top || labelRect.Bottom > rect.Bottom || occupied.Any(r => r.IntersectsWith(labelRect))) continue;
                    occupied.Add(labelRect);
                    g.FillRectangle(panelBrush, labelRect);
                    using var brush = new SolidBrush(color);
                    g.DrawString(label, font, brush, labelRect, format);
                    if (Math.Abs(top + lineHeight / 2f - priceY) > 2)
                        g.DrawLine(pen, labelRect.Left - 8, priceY, labelRect.Left, top + lineHeight / 2f);
                    break;
                }
            }
        }
        finally { g.Restore(saved); }
    }

    private static ScoredZone[] DisplayZones(IReadOnlyList<ScoredZone>? zones, DateTime rightTime, RenderSettings style, int count)
        => zones?.Where(z => z.KnownAtUtc <= rightTime && z.Score.Total >= style.MinimumScore &&
                TierWidth(z.Tier) >= TierWidth(style.MinimumTier)).Take(count).ToArray() ?? [];

    private static List<string> BuildPanel(AdapterView view, LevelSnapshot? raw, ScoredLevelSnapshot? scored,
        RenderSettings style, bool mismatched, DateTime rightTime, ScoredZone[] buy, ScoredZone[] sell, ScoredZone[] watch)
    {
        var lines = new List<string> { $"NQZ6 importance | bias {raw?.Bias ?? style.Bias} | {view.Health}" };
        if (mismatched) lines.Add("NQZ6 SOURCE / DIFFERENT CHART INSTRUMENT - raw NQ points");
        lines.Add($"Source: {raw?.Source.Name ?? "NQZ6 unresolved"} | {view.Connection}");
        if (raw != null)
        {
            lines.Add($"Last {raw.LatestSourcePrice?.ToString("F2") ?? "unknown"} | {raw.LastSourceUpdateUtc:MM-dd HH:mm:ss} UTC | raw v{raw.Version}");
            lines.Add($"As-of {raw.AsOfUtc:MM-dd HH:mm:ss} UTC | {scored?.ScoringVersion ?? "score pending"} | {raw.References.Count} raw refs / {scored?.Zones.Count ?? 0} zones");
        }
        lines.Add("Importance 0-100 = reference evidence, not probability | A Critical 4px / B Major 3px / C Secondary 2px / D Minor 1px");
        lines.Add("Dashed = developing | gray/subdued = watch, inactive or permission blocked | distance in NQ points");
        var remaining = style.TableRows;
        AddSection("BUY - ranked breakout geometry", buy);
        AddSection("SELL - ranked breakdown geometry", sell);
        if (style.ShowContext) AddSection("WATCH - at-level / inactive / blocked", watch);
        if (scored != null && scored.Zones.Any(z => z.KnownAtUtc > rightTime))
            lines.Add("Current strengths after viewport end are hidden; raw rays retain their own known-at gate");
        if (scored != null)
            lines.Add($"Displayed {buy.Length + sell.Length + watch.Length}/{scored.Zones.Count} zones | filter >= {style.MinimumTier}, >= {style.MinimumScore:0} | score known {scored.KnownAtUtc:MM-dd HH:mm:ss} UTC");
        // Keep scan/coverage/time limitations visible instead of hiding them in one clipped line.
        var optionLine = "";
        foreach (var part in view.Options.Split(';'))
        {
            if (optionLine.Length + part.Length > 125 && optionLine.Length > 0)
            { lines.Add(optionLine); optionLine = "  "; }
            optionLine += (optionLine.Length > 2 ? "; " : "") + part.Trim();
        }
        if (optionLine.Length > 0) lines.Add(optionLine);
        lines.Add(view.Profile);
        if (!string.IsNullOrWhiteSpace(view.Error)) lines.Add(view.Error);
        if (style.DetailedHealth)
        {
            lines.Add(view.Coverage);
            if (raw != null) lines.AddRange(raw.ProviderStatus.Where(p => p.Key is not "Source" and not "Options" and not "VolumeProfile")
                .Select(p => $"{p.Key}: {p.Value}"));
        }
        return lines;

        void AddSection(string title, ScoredZone[] zones)
        {
            lines.Add(title + (zones.Length == 0 ? " | none within view filters / known-at" : " | rank tier score | band | reasons | state/mode | distance"));
            for (var i = 0; i < zones.Length && remaining > 0; i++, remaining--)
            {
                var zone = zones[i];
                var components = style.ComponentDetails
                    ? $" [B {zone.Score.Base:0.#} + C {zone.Score.Confluence:0.#} + O {zone.Score.Options:0.#} + P {zone.Score.Profile:0.#}]" : "";
                lines.Add($"{i + 1}. {zone.Tier} {zone.Score.Total:0.#} | {Band(zone)} | {Reasons(zone)} | {State(zone)}{Badges(zone)} | d {zone.Distance:0.00}{components}");
            }
        }
    }

    private static int TierWidth(ImportanceTier tier) => tier switch { ImportanceTier.A => 4, ImportanceTier.B => 3, ImportanceTier.C => 2, _ => 1 };
    private static string Band(ScoredZone zone) => zone.LowPrice == zone.HighPrice ? $"{zone.AnchorPrice:F2}" : $"{zone.LowPrice:F2}-{zone.HighPrice:F2}";
    private static string State(ScoredZone zone) => zone.Inactive ? "inactive/watch" : !zone.ManualPermission || zone.Side == MechanicalSide.AtLevel
        ? "watch" : zone.Side == MechanicalSide.BreakoutBuy ? "BUY" : "SELL";
    private static string Reasons(ScoredZone zone)
    {
        var labels = zone.Members.Select(r => r.Label).Distinct().ToArray();
        return string.Join(" + ", labels.Take(3)) + (labels.Length > 3 ? $" +{labels.Length - 3}" : "");
    }
    private static string Badges(ScoredZone zone) => (zone.Developing ? " developing" : "") +
        (zone.QualityBadges.Count == 0 ? "" : " [" + string.Join(", ", zone.QualityBadges) + "]");
}
