using System.Drawing;
using System.Drawing.Drawing2D;
using NqCandidateLevels.Core;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace NqCandidateLevels;

internal sealed record RenderSettings(Color Buy, Color Sell, Color Context, int Width, int FontSize,
    bool ShowContext, bool Labels, int TableRows, bool DetailedHealth, ManualBias Bias);

/// <summary>Painting only reads an immutable view and native chart coordinates. No data requests or price-scale changes.</summary>
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
            using var panelBrush = new SolidBrush(Color.FromArgb(220, 20, 24, 31));
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            var lineHeight = (int)Math.Ceiling(font.GetHeight(g)) + 3;
            var snapshot = view.Snapshot;
            var mismatched = snapshot != null && (snapshot.Source.ContractId != hostId || snapshot.Source.ConnectionId != hostConnection);
            if (snapshot != null && window != null)
            {
                var converter = window.CoordinatesConverter;
                var rightTime = converter.GetTime(rect.Right);
                var labelY = (float)(rect.Top - lineHeight);
                var visible = snapshot.Rendering.Select(level => (Level: level, Y: converter.GetChartY(level.DisplayPrice)))
                    .Where(x => double.IsFinite(x.Y) && x.Y >= rect.Top && x.Y < rect.Bottom).OrderBy(x => x.Y);
                foreach (var entry in visible)
                {
                    // Scrolling before a value's known-time cannot project its current version backwards.
                    var references = entry.Level.References.Where(r => r.KnownAtUtc <= rightTime).ToArray();
                    if (references.Length == 0) continue;
                    var activeBuy = references.Any(r => !r.Inactive && r.ManualPermission && r.Side == MechanicalSide.BreakoutBuy);
                    var activeSell = references.Any(r => !r.Inactive && r.ManualPermission && r.Side == MechanicalSide.BreakdownSell);
                    if (!style.ShowContext && !activeBuy && !activeSell) continue;
                    var color = activeBuy ? style.Buy : activeSell ? style.Sell : style.Context;
                    var knownAt = references.Min(r => r.KnownAtUtc);
                    var rawX = converter.GetChartX(knownAt);
                    if (!double.IsFinite(rawX) || rawX >= rect.Right) continue;
                    var x = (float)Math.Clamp(rawX, rect.Left, rect.Right);
                    var y = (float)entry.Y;
                    using var pen = new Pen(color, style.Width) { DashStyle = references.Any(r => r.Developing) ? DashStyle.Dash : DashStyle.Solid };
                    g.DrawLine(pen, x, y, rect.Right, y);
                    if (!style.Labels) continue;
                    var label = $"{string.Join(" / ", references.Select(r => r.Label).Distinct())} {entry.Level.DisplayPrice:F2}";
                    if (references.Any(r => r.Developing)) label += " (developing)";
                    var nextY = Math.Max(y - lineHeight / 2, labelY + lineHeight);
                    if (nextY + lineHeight > rect.Bottom) continue;
                    labelY = nextY;
                    var width = Math.Min(rect.Width * .60f, g.MeasureString(label, font).Width + 8);
                    var labelRect = new RectangleF(rect.Right - width, nextY, width, lineHeight);
                    g.FillRectangle(panelBrush, labelRect);
                    using var brush = new SolidBrush(color);
                    g.DrawString(label, font, brush, labelRect, format);
                    if (Math.Abs(nextY + lineHeight / 2 - y) > 2)
                        g.DrawLine(pen, rect.Right - width - 8, y, rect.Right - width, nextY + lineHeight / 2);
                }
            }
            var lines = new List<string> { $"NQZ6 Candidate Levels | bias {snapshot?.Bias ?? style.Bias}", view.Health };
            if (mismatched) lines.Add("NQZ6 SOURCE / DIFFERENT CHART INSTRUMENT — raw NQ points");
            lines.Add($"Source: {snapshot?.Source.Name ?? "NQZ6 unresolved"} | {view.Connection}");
            if (snapshot != null)
            {
                lines.Add($"Last {snapshot.LatestSourcePrice?.ToString("F2") ?? "unknown"} | update {snapshot.LastSourceUpdateUtc:MM-dd HH:mm:ss} UTC");
                lines.Add($"As-of {snapshot.AsOfUtc:yyyy-MM-dd HH:mm:ss} UTC | version {snapshot.Version}");
                lines.Add($"Active {snapshot.ActiveCandidates.Count} / {snapshot.References.Count} references / {snapshot.Rendering.Count} prices");
            }
            lines.Add(view.Coverage);
            lines.Add(view.Options);
            lines.Add(view.Profile);
            if (!string.IsNullOrWhiteSpace(view.Error)) lines.Add(view.Error);
            var table = snapshot?.Rendering.OrderBy(r => Math.Abs(r.DisplayPrice - (snapshot.LatestSourcePrice ?? 0)))
                .Take(style.TableRows).OrderByDescending(r => r.DisplayPrice).ToArray() ?? [];
            if (table.Length > 0)
            {
                lines.Add("NQ price | references | state / known-at UTC");
                lines.AddRange(table.Select(r => $"{r.DisplayPrice:F2} | {r.Label} | " +
                    $"{(r.HasActiveBuy ? "BUY geometry" : r.HasActiveSell ? "SELL geometry" : "context/watch")}{(r.Developing ? " developing" : "")} {r.KnownAtUtc:MM-dd HH:mm}"));
                if (snapshot!.Rendering.Count > table.Length) lines.Add($"Showing nearest {table.Length}/{snapshot.Rendering.Count} prices; increase level table rows to inspect more");
            }
            if (style.DetailedHealth && snapshot != null)
                lines.AddRange(snapshot.ProviderStatus.Where(p => p.Key is not "Source" and not "Options" and not "VolumeProfile")
                    .Select(p => $"{p.Key}: {p.Value}"));
            var availableRows = Math.Max(1, (rect.Height - 12) / lineHeight);
            var shown = lines.Take(availableRows).ToList();
            if (shown.Count < lines.Count && shown.Count > 0) shown[^1] = $"Panel clipped ({shown.Count}/{lines.Count} rows); enlarge chart or reduce font/provider details";
            var panelWidth = Math.Min(rect.Width - 12, mismatched ? 750 : 640);
            var panel = new Rectangle(rect.Left + 6, rect.Top + 6, panelWidth, shown.Count * lineHeight + 8);
            g.FillRectangle(panelBrush, panel);
            for (var i = 0; i < shown.Count; i++)
                g.DrawString(shown[i], font, textBrush, new RectangleF(panel.Left + 4, panel.Top + 4 + i * lineHeight, panel.Width - 8, lineHeight), format);
        }
        finally { g.Restore(saved); }
    }
}
