using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NqCandidateLevels.Core;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Integration;

namespace NqCandidateLevels;

public sealed record OptionsProviderSettings(int SeriesLimit = 8, int StrikesEachSide = 3, int TimeoutSeconds = 90)
{
    public double BandHalfWidth { get; init; } = 500;
    // In-band option symbols are attached in bounded batches so the feed delivers their OI.
    // Each batch is released before the next opens; the request deadline bounds the whole scan.
    public int HydrationBatchSize { get; init; } = 128;
    public TimeSpan HydrationBatchWait { get; init; } = TimeSpan.FromSeconds(3);
}
public sealed record ProfileProviderSettings(ProfileSettings Geometry, int TimeoutSeconds = 60);

public sealed record OptionsProviderSnapshot(
    SourceIdentity Source,
    DateTime ObservedUtc,
    DateTime RequestedAsOfUtc,
    OptionSelectionResult Selection,
    string Status)
{
    // All verified strikes of the requested series, not just the initial price neighborhood.
    // Pure selection can follow source-price changes without new metadata requests.
    public IReadOnlyList<OptionContract> VerifiedContracts { get; init; } = Array.Empty<OptionContract>();
    public string CompatibilityStatus { get; init; } = "";
    public double ScanCenter { get; init; }
    public double ScanHalfWidth { get; init; } = 500;
    public IReadOnlyList<string> ScannedSeriesIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UnqueriedSeriesIds { get; init; } = Array.Empty<string>();
    public bool KnownEntitlementTruncated { get; init; }
    public string HydrationStatus { get; init; } = "no option subscriptions";
}

public sealed record ProfileProviderSnapshot(
    SourceIdentity Source,
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime ObservedUtc,
    ProfileResult Profile,
    string Status)
{
    // Capability refusals remain terminal for this source/session; connection or settings
    // changes can establish new capabilities without repeated refused SDK requests.
    public bool Retryable { get; init; } = true;
    public ProfileCoverageResult? Coverage { get; init; }
}

/// <summary>
/// Bounded, independently sourced optional requests. The adapter owns refresh/cache policy;
/// these workers own and dispose only the histories and option handlers they create. Option
/// symbols inside the analysis band are attached in bounded batches only while a request runs.
/// </summary>
public static class OptionalProviders
{
    public static Task<OptionsProviderSnapshot> GetOptionsAsync(
        Symbol source, SourceIdentity identity, double sourcePrice, DateTime asOfUtc,
        OptionsProviderSettings settings, CancellationToken token)
    {
        return Task.Run(async () =>
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 120));
            bounded.CancelAfter(timeout);
            // Leave room to apply partial hydration before the whole request times out.
            var hydrationDeadlineUtc = DateTime.UtcNow + timeout - TimeSpan.FromSeconds(2);
            var requestToken = bounded.Token;
            var observedUtc = DateTime.UtcNow;
            try
            {
                ValidateSource(source, identity);
                RequireUtc(asOfUtc, nameof(asOfUtc));
                if (!double.IsFinite(sourcePrice) || sourcePrice <= 0)
                    return OptionsUnavailable(identity, observedUtc, asOfUtc, "options unavailable: no valid NQZ6 source price");
                requestToken.ThrowIfCancellationRequested();

                // Request parameters explicitly retain both the futures contract and its connection.
                var allSeries = TradingPlatform.BusinessLayer.Core.Instance.GetOptionSeries(new GetOptionSeriesRequestParameters
                {
                    ConnectionId = identity.ConnectionId,
                    ExchangeId = source.ExchangeId,
                    UnderlierId = identity.ContractId,
                    CancellationToken = requestToken
                });
                requestToken.ThrowIfCancellationRequested();
                if (allSeries == null || allSeries.Count == 0)
                    return OptionsUnavailable(identity, DateTime.UtcNow, asOfUtc, "options unavailable: feed returned no NQZ6 series");
                if (allSeries.Count > 2000)
                    return OptionsUnavailable(identity, DateTime.UtcNow, asOfUtc, "options unavailable: series response exceeds safety bound");

                var metadataAsOfUtc = DateTime.UtcNow;
                var underlying = new UnderlyingVerifier(source, identity, requestToken);
                var seriesLimit = Math.Clamp(settings.SeriesLimit, 1, 8);
                var candidates = allSeries
                    .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Id) && s.ConnectionId == identity.ConnectionId &&
                        underlying.ResolveId(s.UnderlierId) == OptionUnderlyingEvidence.MatchesSource)
                    .Where(s => ValidExpiry(s.ExpirationDate))
                    .OrderBy(s => SdkUtc(s.ExpirationDate)).ThenBy(s => s.Id, StringComparer.Ordinal)
                    .ToArray();
                // Same-day date-only probes consume the same bounded scan budget.
                // Exact contract validation still refuses an unresolved same-day cutoff.
                var universe = OptionUniverseSelection.Select(candidates.Select(s => SeriesMetadata(s, underlying)),
                    identity, metadataAsOfUtc, seriesLimit);
                var selectedIds = universe.SelectedSeries.Select(s => s.SeriesId).ToHashSet(StringComparer.Ordinal);
                var series = candidates.Where(s => selectedIds.Contains(s.Id)).DistinctBy(s => s.Id, StringComparer.Ordinal).ToArray();
                if (series.Length == 0)
                    return OptionsUnavailable(identity, DateTime.UtcNow, asOfUtc, "options unavailable: no unexpired series with verified NQZ6 underlying/connection");

                var rows = new List<(OptionContract Contract, Symbol Symbol)>();
                var rejected = 0;
                var rejectionReasons = new Dictionary<string, int>(StringComparer.Ordinal);
                var inheritedUnderlying = 0;
                var inheritedExpiry = 0;
                foreach (var serie in series)
                {
                    var requestedSeries = SeriesMetadata(serie, underlying);
                    requestToken.ThrowIfCancellationRequested();
                    var strikes = TradingPlatform.BusinessLayer.Core.Instance.GetStrikes(new GetStrikesRequestParameters
                    {
                        ConnectionId = identity.ConnectionId,
                        UnderlierId = identity.ContractId,
                        SerieId = serie.Id,
                        ExpirationDate = serie.ExpirationDate,
                        CancellationToken = requestToken
                    });
                    requestToken.ThrowIfCancellationRequested();
                    if (strikes == null) continue;
                    if (strikes.Count > 20000)
                        return OptionsUnavailable(identity, DateTime.UtcNow, asOfUtc, "options unavailable: strikes response exceeds safety bound");
                    foreach (var option in strikes)
                    {
                        requestToken.ThrowIfCancellationRequested();
                        var compatibility = OptionContractCompatibility.Validate(OptionMetadata(option, underlying),
                            requestedSeries, identity, metadataAsOfUtc);
                        if (!compatibility.Accepted)
                        {
                            rejected++;
                            foreach (var reason in compatibility.Reasons)
                                rejectionReasons[reason] = rejectionReasons.GetValueOrDefault(reason) + 1;
                            continue;
                        }
                        if (compatibility.UsedRequestUnderlying) inheritedUnderlying++;
                        if (compatibility.UsedSeriesExpiry) inheritedExpiry++;
                        // Cached positive fields are supplied values; zero/default values without a
                        // field-validity indicator remain unknown. Retrieval is an observation time,
                        // not a quote time or an OI publication time. No publication clock exists here.
                        var metricObservedUtc = DateTime.UtcNow;
                        var oiValue = PositiveSuppliedValue(option.OpenInterest);
                        var volumeValue = option.VolumeType == SymbolVolumeType.Volume ? PositiveSuppliedValue(option.Volume) : null;
                        rows.Add((new OptionContract(
                            option.Id, identity.ConnectionId, identity.ContractId, serie.Id,
                            compatibility.ExpiryUtc!.Value, option.StrikePrice,
                            option.OptionType == OptionType.Call ? OptionRight.Call : OptionRight.Put,
                            oiValue, volumeValue, metricObservedUtc, null)
                        {
                            OpenInterestMetric = new(oiValue, oiValue.HasValue ? OptionMetricAvailability.CachedValue : OptionMetricAvailability.Unknown, metricObservedUtc),
                            VolumeMetric = new(volumeValue, volumeValue.HasValue ? OptionMetricAvailability.CachedValue : OptionMetricAvailability.Unknown, metricObservedUtc)
                        }, option));
                    }
                }
                requestToken.ThrowIfCancellationRequested();
                var scanHalfWidth = Math.Clamp(settings.BandHalfWidth, 1, 2000);
                // Unsubscribed option symbols keep default fields, which the scorer treats as unknown
                // (0% bucket coverage). Attach the analysis band briefly so the feed can deliver OI.
                var (contracts, hydrationStatus) = await OptionBandHydration.HydrateAsync(rows, sourcePrice, scanHalfWidth,
                    settings.HydrationBatchSize, settings.HydrationBatchWait, hydrationDeadlineUtc - DateTime.UtcNow,
                    requestToken).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                observedUtc = DateTime.UtcNow;
                // Selection/normalization belongs to the adapter's cached pure analysis.
                var status = $"options NQZ6 verified: {series.Length}/{candidates.Length} series scanned, {contracts.Length} contracts; " +
                    $"{universe.UnqueriedSeries.Count} unqueried; cached OI publication unknown; volume session unverified; {hydrationStatus}";
                var selection = new OptionSelectionResult(status, series.Length, Array.Empty<OptionStrike>());
                var diagnostics = new List<string>();
                if (inheritedUnderlying != 0) diagnostics.Add($"{inheritedUnderlying} underlying identities verified by requested series");
                if (inheritedExpiry != 0) diagnostics.Add($"{inheritedExpiry} expiries verified by matching series");
                if (underlying.AliasCount != 0) diagnostics.Add($"{underlying.AliasCount} underlying aliases resolved");
                if (rejected != 0) diagnostics.Add($"{rejected} contracts excluded ({string.Join(", ", rejectionReasons.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}: {r.Value}"))})");
                var compatibilityStatus = string.Join("; ", diagnostics);
                if (compatibilityStatus.Length != 0) status += "; " + compatibilityStatus;
                return new OptionsProviderSnapshot(identity, observedUtc, asOfUtc, selection, status)
                {
                    VerifiedContracts = Array.AsReadOnly(contracts),
                    CompatibilityStatus = compatibilityStatus,
                    HydrationStatus = hydrationStatus,
                    ScanCenter = sourcePrice,
                    ScanHalfWidth = scanHalfWidth,
                    ScannedSeriesIds = Array.AsReadOnly(series.Select(s => s.Id).ToArray()),
                    UnqueriedSeriesIds = Array.AsReadOnly(universe.UnqueriedSeries.Select(s => s.SeriesId).ToArray())
                };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return OptionsUnavailable(identity, DateTime.UtcNow, asOfUtc, "options unavailable: request timed out");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Exception type is useful capability information without provider messages that
                // might contain connection/account details or credentials.
                return OptionsUnavailable(identity, DateTime.UtcNow, asOfUtc,
                    $"options unavailable: {ex.GetType().Name}");
            }
        }, token);
    }

    public static Task<ProfileProviderSnapshot> GetPreviousRthProfileAsync(
        Symbol source, SourceIdentity identity, DateTime startUtc, DateTime endUtc, DateTime asOfUtc,
        ProfileProviderSettings settings, CancellationToken token)
    {
        return Task.Run(() =>
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 180)));
            var requestToken = bounded.Token;
            HistoricalData? history = null;
            IVolumeAnalysisCalculationProgress? calculation = null;
            try
            {
                ValidateSource(source, identity);
                RequireUtc(startUtc, nameof(startUtc)); RequireUtc(endUtc, nameof(endUtc)); RequireUtc(asOfUtc, nameof(asOfUtc));
                if (endUtc <= startUtc || endUtc > asOfUtc || endUtc - startUtc > TimeSpan.FromHours(8) ||
                    startUtc.Ticks % TimeSpan.TicksPerMinute != 0 || endUtc.Ticks % TimeSpan.TicksPerMinute != 0)
                    return ProfileUnavailable(identity, startUtc, endUtc, "volume profile unavailable: require a completed, bounded RTH interval", false);
                if (source.VolumeType != SymbolVolumeType.Volume)
                    return ProfileUnavailable(identity, startUtc, endUtc, "volume profile unavailable: source traded volume not established (ticks/disabled volume)", false);
                if (source.VolumeAnalysisMetadata == null ||
                    source.VolumeAnalysisMetadata.VolumeAnalysisAvailability != VolumeAnalysisAvailability.Available)
                    return ProfileUnavailable(identity, startUtc, endUtc, "volume profile unavailable: independent source capability not advertised", false);
                // Installed SDK GetAllowedPeriods(true) returns the keys of
                // DownloadingLevelsStepByPeriod: supported native price-level periods.
                // Independent M1 history requires native M1 coverage; do not let an
                // absent native route fall back to the Rithmic-prohibited tick route.
                if (source.VolumeAnalysisMetadata.GetAllowedPeriods(true)?.Contains(Period.MIN1) != true)
                    return ProfileUnavailable(identity, startUtc, endUtc,
                        "volume profile unavailable: feed-native M1 price-level history is not advertised; tick reconstruction disabled", false);
                requestToken.ThrowIfCancellationRequested();
                history = source.GetHistory(new HistoryRequestParameters
                {
                    Symbol = source,
                    SymbolId = identity.ContractId,
                    Aggregation = new HistoryAggregationTime(Period.MIN1, HistoryType.Last),
                    FromTime = startUtc,
                    ToTime = endUtc,
                    CancellationToken = requestToken,
                    ForceReload = true,
                    ExcludeOutOfSession = false
                });
                requestToken.ThrowIfCancellationRequested();
                if (history == null || history.Count == 0)
                    return ProfileUnavailable(identity, startUtc, endUtc, "volume profile unavailable: no independent RTH history");
                if (history.Symbol == null || history.Symbol.Id != identity.ContractId || history.Symbol.ConnectionId != identity.ConnectionId)
                    return ProfileUnavailable(identity, startUtc, endUtc, "volume profile unavailable: independent history identity mismatch");

                var bars = SessionBars(history, startUtc, endUtc);
                var minutes = checked((int)(endUtc - startUtc).TotalMinutes);
                if (bars.Length != minutes || bars.Where((bar, index) => SdkUtc(bar.TimeLeft) != startUtc.AddMinutes(index)).Any())
                    return ProfileUnavailable(identity, startUtc, endUtc,
                        $"volume profile unavailable: incomplete RTH coverage ({bars.Length}/{minutes} minute bars)");
                calculation = history.CalculateVolumeProfile(new VolumeAnalysisCalculationParameters
                {
                    CalculatePriceLevels = true,
                    FilteredVolume = 0, // Preserve all trade sizes in the true volume-at-price total.
                    // Rithmic serves native volume-analysis history and rejects forced
                    // reconstruction from tick history. Let the SDK select its supported route.
                    ForceUsingTickData = false,
                    ForceReload = true,
                    CustomTickSize = identity.TickSize,
                    CustomStep = 1
                });
                if (calculation == null)
                    return ProfileUnavailable(identity, startUtc, endUtc, "volume profile unavailable: calculation not supported", false);
                using var abort = requestToken.Register(() => Abort(calculation));
                calculation.Wait(requestToken);
                requestToken.ThrowIfCancellationRequested();
                if (calculation.IsAborted || calculation.State != VolumeAnalysisCalculationState.Finished)
                    return ProfileUnavailable(identity, startUtc, endUtc, "volume profile unavailable: calculation did not finish");

                var profileMinutes = new List<ProfileMinuteData>(bars.Length);
                foreach (var bar in bars)
                {
                    requestToken.ThrowIfCancellationRequested();
                    var levels = bar.VolumeAnalysisData?.PriceLevels;
                    // Keep both SDK streams: native volume analysis is conserved against its
                    // own Total.Volume, while OHLCV volume remains an independent diagnostic.
                    if (levels?.Count > 20000)
                        return ProfileUnavailable(identity, startUtc, endUtc, "volume profile unavailable: price-level response exceeds safety bound");
                    profileMinutes.Add(new ProfileMinuteData(SdkUtc(bar.TimeLeft), bar.Low, bar.High, bar.Volume,
                        bar.VolumeAnalysisData?.Total?.Volume,
                        levels?.Select(l => new ProfileBin(l.Key, l.Value?.Volume ?? double.NaN)).ToArray()));
                }
                var historyIdentity = new SourceIdentity(history.Symbol.ConnectionId, history.Symbol.Id,
                    history.Symbol.Name, identity.ExpirationUtc, history.Symbol.TickSize);
                var coverage = ProfileCoverageValidation.Validate(identity, historyIdentity, startUtc, endUtc,
                    profileMinutes, ProfileDataOrigin.FeedNativeLast);
                if (!coverage.Complete) return ProfileUnavailable(identity, startUtc, endUtc, coverage.Status);
                var profile = ProfileGeometry.Calculate(coverage.Bins, identity.TickSize, settings.Geometry, true);
                var status = profile.Available
                    ? $"profile complete: {profile.Points.Count} references; {coverage.Status}"
                    : "volume profile unavailable: " + profile.Status;
                return new ProfileProviderSnapshot(identity, startUtc, endUtc, DateTime.UtcNow, profile, status) { Coverage = coverage };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return ProfileUnavailable(identity, startUtc, endUtc, "volume profile unavailable: request timed out");
            }
            catch (OperationCanceledException) { throw; }
            catch (NotSupportedException)
            {
                return ProfileUnavailable(identity, startUtc, endUtc,
                    "volume profile unavailable: independent price-level history is not supported by this feed", false);
            }
            catch (Exception ex) when (ex.Message.Contains("Volume analysis calculation from ticks history is not allowed", StringComparison.OrdinalIgnoreCase))
            {
                return ProfileUnavailable(identity, startUtc, endUtc,
                    "volume profile unavailable: feed rejected tick-based volume analysis; no synthetic fallback", false);
            }
            catch (Exception ex)
            {
                return ProfileUnavailable(identity, startUtc, endUtc, $"volume profile unavailable: {ex.GetType().Name}");
            }
            finally
            {
                if (calculation != null && calculation.State != VolumeAnalysisCalculationState.Finished) Abort(calculation);
                history?.Dispose();
            }
        }, token);
    }

    private static HistoryItemBar[] SessionBars(HistoricalData history, DateTime startUtc, DateTime endUtc)
    {
        var items = new List<HistoryItemBar>();
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i, SeekOriginHistory.Begin] is HistoryItemBar bar &&
                SdkUtc(bar.TimeLeft) >= startUtc && SdkUtc(bar.TimeLeft) < endUtc)
                items.Add(bar);
        }
        // A repeated timestamp represents a correction, not additional traded volume.
        return items.GroupBy(b => b.TimeLeft).Select(g => g.Last()).OrderBy(b => b.TimeLeft).ToArray();
    }

    private static OptionCompatibilityMetadata OptionMetadata(Symbol? option, UnderlyingVerifier underlying)
    {
        if (option == null) return new OptionCompatibilityMetadata();
        return new OptionCompatibilityMetadata
        {
            ContractId = option.Id,
            ConnectionId = option.ConnectionId,
            IsOptionSymbol = option.SymbolType == SymbolType.Options,
            Strike = option.StrikePrice,
            Right = option.OptionType == OptionType.Call ? OptionRight.Call :
                option.OptionType == OptionType.Put ? OptionRight.Put : OptionRight.Unknown,
            Expiry = ExpiryMetadata(option.ExpirationDate),
            UnderlyingId = underlying.ResolveId(option.UnderlierId),
            UnderlyingObject = underlying.ResolveObject(option.Underlier),
            DeclaredSeries = option.OptionSerie == null ? null : SeriesMetadata(option.OptionSerie, underlying),
            ReturnedByRequestedSeries = true
        };
    }

    private static OptionSeriesCompatibilityMetadata SeriesMetadata(OptionSerie serie, UnderlyingVerifier underlying) => new()
    {
        SeriesId = serie.Id,
        ConnectionId = serie.ConnectionId,
        Underlying = underlying.ResolveId(serie.UnderlierId),
        Expiry = ExpiryMetadata(serie.ExpirationDate)
    };

    private static OptionExpiryMetadata? ExpiryMetadata(DateTime expiry)
    {
        if (expiry == default) return null;
        // The SDK represents calendar expirations with DateTime too. Midnight values
        // carry no usable intraday cutoff; retain the supplied calendar date separately.
        // Matching verified series can supply an actual time, otherwise midnight is
        // the conservative cutoff rather than an invented end-of-day timestamp.
        return new(SdkUtc(expiry), expiry.TimeOfDay == TimeSpan.Zero, DateOnly.FromDateTime(expiry));
    }

    private sealed class UnderlyingVerifier(Symbol source, SourceIdentity identity, CancellationToken token)
    {
        private readonly Dictionary<string, OptionUnderlyingEvidence> cache = new(StringComparer.Ordinal);
        public int AliasCount { get; private set; }

        public OptionUnderlyingEvidence ResolveId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return OptionUnderlyingEvidence.Missing;
            if (id == identity.ContractId) return OptionUnderlyingEvidence.MatchesSource;
            if (cache.TryGetValue(id, out var found)) return found;
            if (cache.Count >= 256) return OptionUnderlyingEvidence.Unresolved;
            token.ThrowIfCancellationRequested();
            Symbol? resolved;
            try
            {
                resolved = TradingPlatform.BusinessLayer.Core.Instance.GetSymbol(
                    new GetSymbolRequestParameters { SymbolId = id, CancellationToken = token }, identity.ConnectionId);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw; }
            catch { resolved = null; }
            var result = resolved == null ? OptionUnderlyingEvidence.Unresolved : ResolveObject(resolved);
            // Resolving an ID must produce affirmative exact-contract evidence.
            if (result == OptionUnderlyingEvidence.Missing) result = OptionUnderlyingEvidence.Unresolved;
            cache[id] = result;
            if (result == OptionUnderlyingEvidence.MatchesSource) AliasCount++;
            return result;
        }

        public OptionUnderlyingEvidence ResolveObject(Symbol? candidate)
        {
            if (candidate == null) return OptionUnderlyingEvidence.Missing;
            if (ReferenceEquals(candidate, source)) return OptionUnderlyingEvidence.MatchesSource;
            // A cached object is verified independently; a shared NQ root/display alias
            // alone never establishes the December 2026 futures contract.
            if (SourceResolver.Validate(candidate, identity.ConnectionId, out _))
                return OptionUnderlyingEvidence.MatchesSource;
            if (string.IsNullOrWhiteSpace(candidate.Id) && string.IsNullOrWhiteSpace(candidate.Name) &&
                string.IsNullOrWhiteSpace(candidate.ConnectionId)) return OptionUnderlyingEvidence.Missing;
            return OptionUnderlyingEvidence.ConflictsWithSource;
        }
    }

    private static void ValidateSource(Symbol source, SourceIdentity identity)
    {
        if (source == null || source.SymbolType != SymbolType.Futures || source.Id != identity.ContractId ||
            source.ConnectionId != identity.ConnectionId || !double.IsFinite(identity.TickSize) || identity.TickSize <= 0 ||
            !double.IsFinite(source.TickSize) || Math.Abs(source.TickSize - identity.TickSize) > 1e-9)
            throw new ArgumentException("Optional requests require the independently validated source contract.");
    }

    private static bool ValidExpiry(DateTime expiry) => expiry.Year >= 2020 && expiry.Year <= 2100;
    private static double? PositiveSuppliedValue(double value) => double.IsFinite(value) && value > 0 ? value : null;
    private static void RequireUtc(DateTime time, string name)
    {
        if (time.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC timestamp required", name);
    }
    private static DateTime SdkUtc(DateTime time) => time.Kind == DateTimeKind.Local
        ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);
    private static void Abort(IVolumeAnalysisCalculationProgress calculation)
    {
        try { calculation.AbortLoading(); } catch { /* Cancellation callback must not throw into the caller. */ }
    }
    private static OptionsProviderSnapshot OptionsUnavailable(SourceIdentity source, DateTime observed, DateTime asOf, string status)
        => new(source, observed, asOf, new OptionSelectionResult(status, 0, Array.Empty<OptionStrike>()), status);
    private static ProfileProviderSnapshot ProfileUnavailable(SourceIdentity source, DateTime start, DateTime end, string status, bool retryable = true)
        => new(source, start, end, DateTime.UtcNow, new ProfileResult(false, status, Array.Empty<ProfilePoint>()), status) { Retryable = retryable };
}
