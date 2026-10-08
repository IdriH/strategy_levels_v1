# NQZ6 Candidate Levels

Quantower main-chart indicator for the **December 2026 E-mini Nasdaq-100 futures contract** on a selected Rithmic connection. The host chart supplies only painting coordinates. Price observations, Last/M1 history, option underlying validation and optional volume profile use the separately resolved NQZ6 symbol. This milestone generates references and mechanical buy/sell geometry; it contains no trading or scoring.

## Build and install

Detected here on **7 October 2026**: Quantower **1.146.18**, installed at `C:\Quantower`, SDK at `C:\Quantower\TradingPlatform\v1.146.18\bin`, with .NET 10 and Windows Desktop runtime. The existing Algo project/assembly name is retained. Build output stays in this workspace until you run the installer.

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Find-Quantower.ps1 -InstallationRoot C:\Quantower
dotnet build .\candidate_levels_v1_1.slnx -c Release
dotnet run --project .\NqCandidateLevels.Tests\NqCandidateLevels.Tests.csproj -c Release
```

`Find-Quantower.ps1` validates actual installed assemblies and writes ignored `Quantower.local.props`. Its execution policy applies only to that process. After a terminal upgrade, rerun discovery. A different installation can be supplied through `-InstallationRoot`, `QUANTOWER_ROOT`, `QUANTOWER_SDK_PATH`, or MSBuild:

```powershell
dotnet build .\candidate_levels_v1_1\candidate_levels_v1_1.csproj -c Release -p:QuantowerSdkPath="D:\YourActualTerminal\TradingPlatform\vX.Y.Z\bin"
```

To install using the detected Quantower Algo directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-Indicator.ps1 -InstallationRoot C:\Quantower
```

This rebuilds and copies this indicator's DLL, pure-core DLL, symbols and dependency manifest into `C:\Quantower\Settings\Scripts\Indicators\candidate_levels_v1_1`, the directory referenced by the original Algo project. It does not copy SDK assemblies or modify other indicators. Remove this indicator and restart Quantower after installation to reload both assemblies, then add **NQZ6 Candidate Levels** to a chart. In Visual Studio, open the existing `.slnx`, select Release, and build; use the script for deployment. `Directory.Build.targets` keeps output/reference paths configurable even when the Quantower Algo extension rewrites `.csproj` paths. The installed terminal's .NET 10 desktop runtime supplies System.Drawing.

## Source and settings

Set the **Rithmic connection ID** if more than one active Rithmic connection exists. A blank ID works only with exactly one active connection. Status lists matching connection names, IDs and states when selection is ambiguous. Load NQZ6 through Quantower's symbol lookup first, or enter its exact provider contract ID. The resolver requires the selected connection, futures type, exact NQZ6/NQZ26 spelling, December 2026 expiration/maturity metadata, CME venue and 0.25-point tick. Continuous contracts, spreads, MNQ, other expiries and unverified metadata are rejected. Resolved metadata is logged without connection settings or credentials.

Settings are grouped into permissions, independent source, calendar, price families, OR, structure, profile, options, rendering and diagnostics. Bias defaults to **Both**. BuyOnly/SellOnly filter geometry relative to the latest valid NQZ6 price; NoTrade has no active directional candidates. Within **0.5 NQ ticks** by default, a reference is an at-level watch. Disabled directions can remain grey context. Bias/style changes reuse data; changes to source/calculation settings cancel and rebuild owned work.

Lookback defaults to **30 calendar days**, M1 Last history, with a 45-second bootstrap deadline. Calculation follows observed source market time, while a separate timer checks wall-clock feed health and requests repaint without host-chart ticks. Open-market data older than 90 seconds is inactive by default. Closed market, stale open market, disconnected, loading, unresolved and uncertain schedule states are distinguished. This release does not claim replay-clock support.

## Providers and formulas

| Provider | References and availability |
|---|---|
| Previous RTH | High/low and last completed M1 close of the most recent completed cash session. Rolls at its end once source observations reach that boundary. |
| Previous full futures session | High/low and last completed M1 close over scheduled exchange intervals, excluding closures. Rolls once observed source time reaches session end. |
| Overnight | High/low from futures start through cash open; develops on completed M1 bars and freezes at cash open. |
| Trading week | Current high/low and first valid start-minute open; previous completed week's high/low. Monday–Friday exchange reference dates include Sunday evening. Current extrema carry the time of the completed observations used. |
| Current RTH | First cash-minute open and **VWAP proxy**: sum of `((high + low + close)/3) × traded volume` divided by traded volume. No exact trade VWAP is claimed. |
| Opening range | First 15 cash minutes by default; final high/low are absent until the whole range completes. Optional developing display is off by default. |
| Structure | Default 2 left/2 right completed M1 bars. Equal extrema favor the earliest pivot (strict left, inclusive right). Rays begin at confirmation, bounded by lookback and count. |
| Profile, off by default | Previous completed RTH POC/HVN/LVN from independently requested real SDK price-level volume. Full M1 coverage and price-level/native total agreement are required; candle-volume differences remain diagnostics. |
| Options, on by default | Nearest two verified unexpired NQZ6-underlying series; five distinct strikes on each side of source price, plus an exact at-price strike. Cached verified chains are reselected as source price moves. Strike values are index points; call/put and contract provenance are metadata. |

Every required completed minute must be present for a session/week aggregate to be usable. Missing minutes are gaps, never invented no-trade bars. A supplied zero-volume bar is valid price data; zero total volume suppresses VWAP. Unknown candle volume leaves price providers usable and suppresses proxy VWAP; profile availability depends on its independently verified native totals/price levels. Tick-count volume is not traded volume. Missing session/week starts do not produce fabricated opens. Provider status explains suppressed families; strict coverage can reduce displayed references on a feed that omits empty minutes.

Completed bars are keyed by UTC minute, sorted and replaced on correction. Live Last events update source price/time but never add volume already contained in M1 history. Forming bars do not enter finalized calculations. Quotes reuse cached completed aggregates; completed-minute changes rebuild relevant session caches. Corrected values carry a new snapshot version, correction quality and receipt availability. Current weekly/overnight/VWAP values are drawn from their current version's known-time rather than projected backwards.

Profile bins are configurable tick multiples. POC ties choose the lowest price. HVN/LVN use a fixed centered smoothing radius of one bin by default and a configurable strict local neighborhood; plateaus use their lower midpoint. Only internal traded-range minima qualify. Node limits rank histogram geometry, not trade quality. Source-history updates invalidate the profile; fresh independent history is requested with bounded retries. Profiles become available at retrieval completion, including corrected profiles. See [SDK capabilities](docs/sdk-capabilities.md) for field and request limits.

For Rithmic, the provider lets the SDK use the feed's native volume-analysis history (`ForceUsingTickData = false`). It first checks native M1 price-level capability. Actual unfiltered levels at one NQ tick are conserved against each minute's `VolumeAnalysisData.Total.Volume`. The separate OHLCV volume and any disagreement remain visible diagnostics; no volumes are multiplied, allocated across prices or replaced. Validated raw bins and both totals are retained in the profile provider's `Coverage`. Capability refusals stop retrying for the same session; reconnect, calculation settings changes or the next reference session can trigger a new check. Rebuild/install the updated DLLs and reload the indicator to apply the fix. See [Rithmic/Quantower compatibility research and corrections](docs/rithmic-compatibility.md).

Populated options underlying aliases must resolve to fully verified NQZ6 metadata on the same connection; conflicting/unresolved identities remain excluded. Absent child fields can inherit exact verified series-request provenance. Same-date calendar-only expiry metadata can agree with a supplied actual series/option time; unknown cutoffs are never extended to end-of-day. Status retains granular exclusions and metadata compatibility counts. Cached positive OI and traded volume are retained only when supplied; ambiguous default zero values stay nullable. Retrieval time is retained, OI publication time remains unknown. Current chain snapshots become available only at their observation time. No dealer gamma or directional inference is made from those fields.

## Calendar and rendering

Ordinary 2026 sessions use **17:00–16:00 America/Chicago** with the **16:00–17:00 maintenance closure**, and cash RTH **09:30–16:00 America/New_York**. UTC storage and Windows/IANA timezone mapping handle DST. All intervals are `[start,end)`. The historical 15:15–15:30 Chicago equity halt was removed by CME in 2021. Holiday periods and potentially merged reference dates are uncertain until an authoritative local override establishes their intervals; affected families are suppressed. [Calendar documentation](docs/calendar.md) provides the JSON schema and primary sources; [the example file](calendars/nqz6-2026.example.json) is deliberately unverified and must not be enabled unchanged. NQZ6 stops at **2026-12-18 09:30 New York / 14:30 UTC**, without rollover.

Each reference retains raw price, logical ID, snapshot version, connection/contract IDs, source interval/reference date, origin, known-at, computed and last-update times, quality, permissions and optional provenance. `NqZ6CandidateLevelsIndicator.CurrentSnapshot` is an immutable thread-safe snapshot for a later scorer. `References` preserves unfiltered provider data; `ActiveCandidates` applies source health and manual permission. Exact tick-equivalent display prices merge labels/provenance without averaging.

Rays use the native main-pane coordinates and clip to the chart. Developing values are dashed; eligible buy/sell and context colors are configurable. Right labels avoid vertical overlap. The panel/table always reports the current source snapshot as-of time. On ES, MNQ or an option chart, a different-instrument badge appears and raw NQ prices stay unchanged; off-scale rays are omitted and the table remains usable. No host scale, symbol or basis conversion is changed. Current snapshots are not a historical replay archive: scrolling before a value's known-time hides that version's ray.

## Validation

The no-package console suite exercises calendar/DST/boundaries and shortened sessions, OR completion, swing confirmation/ties, developing/frozen references, completed-reference rollover, missing starts/gaps, unknown/zero volume, VWAP corrections and bootstrap/live equivalence, correction receipt timing, calculation-cache reuse, merge/bias modes, options underlying/nullable fields, profile geometry and host independence. Run the command above; failure returns a nonzero exit code.

Live Quantower/Rithmic behavior has not been certified by compilation. Use [the chart validation checklist](docs/chart-validation.md) to verify actual source metadata, feed history, optional permissions, painting, reconnect and removal. A successful build establishes compatibility with the installed SDK signatures; actual optional feed capabilities are shown at runtime.
