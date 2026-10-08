# Installed SDK and optional provider capabilities

Inspected on 7 October 2026: `C:\Quantower\TradingPlatform\v1.146.18\bin\TradingPlatform.BusinessLayer.dll`, assembly version **1.146.18.0**, and its XML documentation. The existing Algo project targets .NET 10; the indicator compiles directly against this installed API. The source code uses direct calls. Reflection is confined to development signature inspection and is never used to make indicator calls.

Reproduce the optional API inspection with:

```powershell
dotnet run --project tools/OptionalInspector -- --sdk "C:\Quantower\TradingPlatform\v1.146.18\bin" OptionSerie GetOptionSeriesRequestParameters GetStrikesRequestParameters HistoricalData IVolumeAnalysisCalculationProgress VolumeAnalysisCalculationParameters IHistoryItem VolumeAnalysisData VolumeAnalysisItem VolumeAnalysisMetadata
```

The inspector accepts any detected version's `bin` directory through `--sdk`, or `QUANTOWER_SDK_PATH`; it does not initialize `Core` or make market-data requests.

## Options

Installed signatures:

```csharp
IList<OptionSerie> Core.GetOptionSeries(GetOptionSeriesRequestParameters requestParameters);
IList<Symbol> Core.GetStrikes(GetStrikesRequestParameters requestParameters);
```

`GetOptionSeriesRequestParameters` exposes `ConnectionId`, `ExchangeId`, `UnderlierId`, and `CancellationToken`. `GetStrikesRequestParameters` exposes `ConnectionId`, `UnderlierId`, `SerieId`, `ExpirationDate`, and `CancellationToken`. Both are used to make bounded requests for the resolved NQZ6 source, with cancellation/deadline tokens. These are the cancellable forms of the methods in Quantower's [official options example](https://github.com/Quantower/Examples/blob/master/IndicatorExamples/GetOptionsInfo.cs).

The local type is **`OptionSerie`**, singular, rather than `OptionSeries`. It exposes `Id`, `ConnectionId`, `UnderlierId`, and `ExpirationDate`. Each selected series must identify the exact source contract and connection. The provider requests the nearest configured future series, two by default, plus at most eight current-date calendar-only series that might obtain a precise cutoff from returned metadata. Final selection still applies the configured series limit. Every returned option must have `SymbolType.Options`, the same connection, a valid strike/expiry and call/put type. Populated underlying IDs are resolved on the selected connection through cancellable `Core.GetSymbol`; aliases must independently pass the complete NQZ6 metadata rules, including CME venue, December 2026 and 0.25 tick. Resolution is cached and bounded to 256 distinct IDs per refresh. Generic NQ roots and conflicting/unresolved fields remain excluded.

Missing child underlying/expiry fields can inherit the context of an independently verified `GetStrikes` series request. Empty cached `OptionSerie` fields are optional; populated series IDs, connections, underlying identities or expiry conflicts still reject the row. Calendar-style midnight expirations retain their original date and can agree with a supplied precise time for that same verified series/date. Two distinct precise times cannot agree merely because they fall on the same day. All-date-only metadata uses a conservative midnight cutoff; no end-of-day time is invented. Option expiry need not equal the futures' December expiry. Status retains compatibility counts and granular exclusion reasons through cached price reselection. The exact subtype of the original 1,714 exclusions cannot be established from the screenshot alone.

No option quote or trade subscriptions are opened. Selection uses five distinct strikes per side of the latest NQZ6 source price by default, including an exactly at-price strike if present. The snapshot retains all verified contracts from the bounded requested series as a read-only list. As source price moves, the adapter reruns the pure selector over that cached list, without chain downloads per quote. Contract observation times still gate availability. The pure selector retains series, expiry, call/put and contract provenance and merges identical strikes. Rendering uses `StrikePrice` in futures index points; premiums are unused.

`Symbol` exposes cached `OpenInterest` and `Volume` fields, plus `QuoteDateTime` and `LastDateTime`. There is no field-specific OI publication clock or field-validity indicator in these signatures. A finite positive cached OI/volume is retained with the local metadata-observation time; a missing/nonfinite/default zero is `null`. Option volume is retained only when that option's `VolumeType` is `SymbolVolumeType.Volume`; tick-count or disabled volume remains unknown. A genuine reported zero cannot be distinguished from an unpopulated default here, so it is conservatively unknown. `OpenInterestPublishedUtc` remains `null`; quote/trade times are not misrepresented as OI publication times. Status reports how many selected contracts have values and the missing publication-time capability.

Options selection occurs at the actual metadata-observation time. The resulting references become available at that time and are never applied retrospectively to source history. The source market time passed to the request is retained separately as `RequestedAsOfUtc`. This release does not establish replayed option chains or a replay clock.

Responses above 2,000 series or 20,000 contracts per requested series are rejected with a visible status. Request cancellation is cooperative through the SDK token; provider permissions, actual chain delivery and field freshness must be checked with the connected feed at runtime. An empty, unverifiable or unsupported chain yields no option references and does not affect futures providers.

## Previous RTH volume profile

Installed signatures:

```csharp
HistoricalData Symbol.GetHistory(HistoryRequestParameters requestParameters);
IVolumeAnalysisCalculationProgress HistoricalData.CalculateVolumeProfile(VolumeAnalysisCalculationParameters parameters);
void IVolumeAnalysisCalculationProgress.Wait(CancellationToken token);
void IVolumeAnalysisCalculationProgress.AbortLoading();
```

`HistoryRequestParameters` has `Symbol`, `SymbolId`, `Aggregation`, `FromTime`, `ToTime`, `CancellationToken`, `ForceReload` and `ExcludeOutOfSession`. The worker creates its own Last/M1 history over exactly the completed previous RTH interval, with an eight-hour maximum interval. `ForceReload = true` requests fresh history after adapter correction invalidation. It never uses the host chart's history or volume-analysis interface.

`VolumeAnalysisCalculationParameters` exposes `CalculatePriceLevels`, `ForceUsingTickData`, `ForceReload`, `CustomTickSize` and `CustomStep`. The worker requests actual SDK/feed-native price levels at the source tick size with step one, `CalculatePriceLevels = true`, `ForceUsingTickData = false`, `ForceReload = true` and `FilteredVolume = 0` to preserve all trade sizes. It does not force reconstruction from tick history. Calculation state exposes `Finished` and `IsAborted`; cancellation also invokes `AbortLoading`. `IHistoryItem.VolumeAnalysisData.PriceLevels` is a `Dictionary<double, VolumeAnalysisItem>` with actual `Volume` per price. These installed signatures agree with the [HistoricalData documentation](https://api.quantower.com/docs/TradingPlatform.BusinessLayer.HistoricalData.html) and [volume-analysis parameters documentation](https://api.quantower.com/docs/TradingPlatform.BusinessLayer.VolumeAnalysisCalculationParameters.html).

Coverage is deliberately conservative. The source must advertise native M1 volume analysis as available and `VolumeType` must be `SymbolVolumeType.Volume`; tick-count/disabled volume is rejected. Every expected minute of the half-open session must be present, with valid tick-grid price bounds and price levels inside those bounds. Each minute's level volumes must conserve its SDK-native `VolumeAnalysisData.Total.Volume`, finite and nonnegative, rather than the separate OHLCV `HistoryItemBar.Volume`. Quantower's [Algo guide](https://help.quantower.com/quantower/quantower-algo/access-volume-analysis-data-from-indicators) identifies native `Total` as the aggregate for its `PriceLevels`. OHLCV/native differences or unavailable candle volume remain diagnostics with minute counts and totals; native bins are not scaled. A known positive candle volume with zero native total remains a missing-data failure. Null native totals/levels, missing minutes, invalid bins, native conservation failures and aborted calculations suppress the provider. An explicit empty level collection with zero native total can represent an observed no-trade minute; an entirely zero-volume session produces no profile. The snapshot retains the validated raw bins and separate native/OHLCV totals in `Coverage`. No OHLCV volume is distributed across prices.

Price levels are summed across the session and then passed to the pure profile geometry. Bin width is a configurable integer multiple of source ticks. POC uses the maximum-volume bin, with the lowest-price bin winning ties. HVN/LVN use the configured centered moving-average radius and strict local-neighborhood comparison. Flat plateaus use their lower midpoint. Edges outside the traded range cannot become LVNs. Internal gaps can be minima. Limits select HVNs by descending smoothed volume and LVNs by ascending smoothed volume, then lower price for ties; these are geometry rules without return optimization or quality scores. The histogram is capped at 100,001 price bins.

The worker disposes its owned history after calculation, aborts unfinished owned calculations, and never disposes platform-owned `Symbol` objects. The indicator adapter owns session-cache keys, correction invalidation, bounded refresh/retry and generation guards against late results. Actual Rithmic native price-level coverage and permissions have not been established merely by inspecting/compiling the SDK; chart validation remains necessary.

### Rithmic profile request route

On 8 October 2026 the connected platform reported a refusal of volume-analysis calculation from tick history. The previous worker explicitly set `ForceUsingTickData = true`; that selected the refused route. The fix preserves true volume-at-price and lets the SDK use its supported native route. Quantower documents its dedicated [accelerated Rithmic volume-analysis loading mechanism](https://www.quantower.com/blog/connection-to-deribit-improvements-for-dom-trader-and-option-analystics-panels). This does not guarantee that this account supplies complete M1 price levels.

A metadata preflight now requires advertised M1 price-level history before a calculation is submitted. Installed SDK metadata/IL inspection establishes that `VolumeAnalysisMetadata.GetDownloadingStepByPeriod(true)` returns `DownloadingLevelsStepByPeriod`, and `GetAllowedPeriods(true)` returns the keys of that dictionary. The independent M1 worker therefore requires `Period.MIN1` among those price-level periods. It does not invent a positive downloading-step constraint or request a coarser histogram. Missing native M1 capability yields an explicit unavailable result with `Retryable = false`, without attempting tick reconstruction. The parameter constructor itself defaults `ForceUsingTickData` to false. Inspection uses the development-only `tools/OptionalInspector --metadata-il` mode and invokes no target SDK code.

Unsupported traded volume, unavailable volume-analysis capability, absent native M1 price levels, an unsupported calculation, or an explicit provider refusal are terminal for the current source/session. The adapter can retry when the session, settings or connection changes. Missing history, partial price-level coverage and timeouts remain retryable. Every minute must pass native conservation checks; no OHLCV allocation or other synthetic profile is introduced. See the [8 October compatibility investigation](rithmic-compatibility.md) for primary references and remaining runtime checks.

## Time and scope

Request boundaries must be explicit UTC and obey `[start, end)`. SDK market timestamps carrying `Unspecified` kind are marked UTC, consistent with Quantower market-history conventions; `Local` timestamps are converted to UTC. Session boundaries are supplied by the independent exchange-calendar engine, including shortened sessions. Optional workers do not infer session dates from the host chart or computer timezone.

The attached chart is exclusively a rendering surface. Optional requests take the separately validated source `Symbol` plus `SourceIdentity` and reject contract/connection mismatches before making a request. Compilation verifies signatures and types; it does not certify live feed availability, chain completeness, exchange schedules or runtime subscription cleanup.
