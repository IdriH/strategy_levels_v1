# Implementation verification

Implemented on **7 October 2026** in the existing Quantower Algo workspace.

## Delivered

- `NqZ6CandidateLevelsIndicator`: grouped platform settings, immutable public `CurrentSnapshot`, main-pane clipped rays/right labels/status/table, independent repaint, different-host badge and manual permissions.
- Dedicated exact-contract Rithmic resolver and independent Last/M1 history adapter. Source events drive calculations; host OHLCV, indices, aggregations and built-in indicators are unused.
- Previous cash/full high/low/close, developing/frozen overnight, current/previous week, current cash open, labelled M1 VWAP proxy, finalized/developing OR and completed-bar confirmed swings.
- Optional previous-RTH real volume profile with POC/HVN/LVN geometry and strict coverage; optional verified-underlying strike provider with nullable OI/volume, metadata times and cached reselection.
- UTC/DST exchange calendar, explicit local override schema, uncertainty suppression, maintenance handling and fixed NQZ6 expiry cutoff.
- Cancellation/deadlines, connection-change reload, generation guards, source correction replacement/versioning, profile invalidation, bounded retries, cached calculations and owned-resource teardown.
- Configurable SDK references, local build override, installation discovery/script, calendar/formula documentation and live chart checklist.

## Executed verification

`dotnet build candidate_levels_v1_1.slnx -c Release` passed against **TradingPlatform.BusinessLayer 1.146.18** and .NET 10 with **0 warnings / 0 errors**. All three solution projects built. The indicator assembly is emitted to `candidate_levels_v1_1/bin/Release`, together with `NqCandidateLevels.Core.dll`; SDK/system assemblies are not bundled for terminal deployment.

`dotnet run --project NqCandidateLevels.Tests/NqCandidateLevels.Tests.csproj -c Release --no-build --no-restore` passed **23/23 focused regression groups**. Coverage includes Sunday/Monday/Friday mapping, both US DST changes, half-open and shortened sessions/explicit breaks, uncertain or invalid calendar data, OR gating, swing confirmation/ties, frozen/developing and session/week rollovers, incomplete/start-missing history, true zero versus unknown volume, VWAP/bootstrap/live corrections, correction receipt availability, forming-bar calculation-cache reuse, all bias modes, tick merge, strict source and option underlying validation, nullable OI, real profile geometry and host independence. Added compatibility checks cover series-request provenance, absent versus conflicting option metadata, date-only/precise expiry, granular exclusions, unchanged native profile bins, native volume conservation and independent OHLCV diagnostics.

SDK reflection inspection verified signatures without initializing the platform or downloading data. Actual code uses direct typed APIs. Discovery verified `C:\Quantower\TradingPlatform\v1.146.18\bin`, and MSBuild property inspection confirmed workspace output. Quantower Algo can rewrite project paths in Visual Studio; the separate late `Directory.Build.targets` preserves configurable references and local output.

## Not established by these checks

No terminal deployment, connected-feed/chart execution or live subscription-count test was performed. The README's installer and [chart validation checklist](chart-validation.md) provide the exact next steps. Runtime Rithmic symbol metadata, chain permissions/fields, M1 continuity and true tick price-level coverage remain subject to the connected feed; unsupported optional data reports unavailable and does not disable valid price providers.

Exact trade VWAP, a replay clock/source pipeline and OI publication timestamps were not established. VWAP therefore uses the labelled traded-volume bar proxy; option statistics retain retrieval observations with unknown publication times. No automatic rollover, secondary NQ axis, basis conversion, orders, scoring or prediction is implemented.

Special-session futures calendars cannot be verified from a disconnected SDK assembly. Regular 2026 sessions and the fixed contract termination use the cited exchange rules; holiday periods require verified local overrides. Strict coverage intentionally suppresses aggregates when missing empty minutes cannot be distinguished from incomplete history.

## Rithmic profile correction, 8 October 2026

The user observed Rithmic rejecting volume analysis calculated from historical ticks. The initial request had explicitly set `ForceUsingTickData = true`; the corrected request sets it to `false` so the SDK can use the supported native volume-analysis route. A preflight check requires native M1 price-level metadata. Static PE/IL inspection established that the installed `GetAllowedPeriods(true)` returns the keys of `DownloadingLevelsStepByPeriod`; no platform requests were made by that inspection. Price levels remain requested, the source tick size and step one are retained, and `FilteredVolume = 0` preserves all supplied trade sizes. Both history and profile calculation request fresh data for corrected source history. Minute continuity, tick-grid validation and equality of price-level/bar traded-volume totals still gate availability. Capability failures are non-retryable for the same reference session, while timeouts/incomplete loading retain bounded retries.

The correction changes the loading route rather than replacing the histogram with bar-derived estimates. POC/HVN/LVN continue to require actual volume-at-price. Historical individual-trade timestamps are not reconstructed from native aggregates. Actual native data delivery and warning removal require the connected-feed checks in the chart checklist after rebuilding/installing the updated indicator.

## Native totals and options metadata correction, 8 October 2026

The next live screenshot showed the native request reaching validation, where price-level sums were compared to the separate candle-volume stream. Validation now compares the levels to SDK-native `VolumeAnalysisData.Total.Volume`. Exact source, native M1 capability, full minute coverage, price bounds/tick-grid and native volume conservation still gate the profile. OHLCV differences are diagnostic; all supplied native bins stay unchanged and are retained with both totals in the provider snapshot. Missing native values remain missing. No rescaling or synthetic profile is used.

The options adapter now verifies same-connection underlying aliases using complete exact-contract metadata, permits absent child fields through independently verified series-request provenance and reconciles calendar-only expiries with matching supplied precise timestamps. Populated contradictions still exclude contracts. Same-day uncertain series requests are bounded separately from configured future series. Status preserves granular rejection reasons through cache reselection and reports unavailable when cached contracts expire. The screenshot alone does not establish which old options predicate rejected all rows.

The workspace output override also resets SDK-derived DLL/dependency/runtime-config paths, preventing the Algo extension's project rewrite from redirecting builds into the installation folder. Release build against SDK 1.146.18 passes with zero warnings/errors. [The compatibility investigation](rithmic-compatibility.md) records official Rithmic/Quantower references and live verification steps.
