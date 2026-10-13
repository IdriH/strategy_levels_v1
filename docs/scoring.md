# importance-v1

These deterministic rules measure valid reference evidence concentrated in a price zone. They are hypotheses for observation, not calibrated probabilities, expected returns, gamma, support/resistance guarantees or entry triggers. Source identity, completed-minute/calendar rules and manual permissions remain unchanged. No orders or execution features are included.

## Evidence and zones

`ReferenceKind` and `LevelFamily` define meaning without parsing labels. Each source M1 extreme has an explicit originating-event key including source, origin time, high/low and tick. Session, weekly and swing aliases of that event count once for confluence; all labels remain in provenance.

Exact tick buckets remain available in the raw view. Scored zones sort by integer tick then stable reference ID and extend only while `newTick - minimumTick <= toleranceTicks`. Default tolerance is 8 NQ ticks (2 points), configurable 0–40. Zero groups only tick-equivalent prices. Thus 100, 102, 104 cannot form one zone with a 2-point limit. Bounds and anchor retain actual member prices. The anchor uses greatest valid base weight then stable ID. Membership determines the zone ID.

A band entirely above source price plus at-level tolerance has BUY geometry; entirely below has SELL geometry. Price in or within tolerance of a band is watch context. Bias and source health gate eligibility after intrinsic scoring; NoTrade cannot be overridden by a score.

## Formula and base rules

`Importance = clamp(Base + Confluence + Options + Profile, 0, 100)`.

The denominator stays fixed. Missing components are zero; no weights are redistributed. Ordering uses unrounded values. Developing references multiply their own base by 0.75 before taking the strongest member.

| Reference | Base |
|---|---:|
| Previous completed week high/low | 40 |
| Previous RTH high/low | 38 |
| Current week high/low; previous full session high/low | 36 |
| Overnight high/low; genuine prior RTH POC | 32 |
| Completed OR high/low; eligible OI concentration | 30 |
| Genuine prior RTH HVN/LVN | 28 |
| Current week open | 26 |
| Confirmed swing; previous RTH close; volume-only activity | 24 |
| Previous full session close | 22 |
| Current RTH open | 20 |
| M1 VWAP proxy | 18 |
| Verified nearest unmeasured options context | 10 |

Confluence groups are SessionExtremes, WeeklyReferences, ConfirmedStructure, AuctionReferences and OptionsConcentration. Award 0/10/18/25 points for at most one/two/three/four or five distinct groups after event deduplication. OI options need effective concentration >=0.65 for the options group. Volume-only and context never qualify. Multiple contracts, expiries or profile nodes cannot multiply groups.

Options points = `25 * maximum eligible effective concentration`. Profile points = `10 * maximum validated normalized prominence`. POC/HVN prominence is `clamp(log2(nodeSmoothed / medianPositiveSmoothed),0,1)`. LVN prominence is `clamp(1 - valleySmoothed/min(leftPeakSmoothed,rightPeakSmoothed),0,1)`, using independently detected flanking local maxima; without two valid flanks the bonus is unavailable. The immutable histogram preserves genuine bins, actual smoothing and median; existing POC and plateau tie rules remain. No synthetic OHLCV allocation is used.

Expected example: RTH PDH base 38, independent POC and qualifying OI form three groups (18); effective OI 0.80 gives 20; profile prominence 0.80 gives 8: **84/A**. Three labels of one session extreme alone remain **38/C**.

## Options universe and metrics

Default scan budget 8: nearest four verified series; earliest remaining beyond 7 days; earliest remaining beyond 30 days; latest within source expiry; then earliest remaining. Same-day date-only probes consume this same budget and unresolved cutoffs never become end-of-day expirations. Exact NQZ6 underlier and selected connection are mandatory. Calendar dates do not establish product subtype.

Full supported strike responses are retained, with existing 2,000-series / 20,000-row per-series response limits, cooperative cancellation and epoch guards. Default analysis band half-width is `min(2000,max(500,2*completedPreviousRthRange))`, falling back to 500. Metadata refresh is 120 seconds; retrieval TTL is 300 seconds. Price changes reselect cached peaks; moving outside the band waits for the existing metadata throttle. No chain downloads occur in paint or per tick.

Canonical contracts deduplicate before summation into actual expiry / integer strike buckets. Both call and put membership and all returned unique contract metrics must be valid to enter that metric's sample. Partial lower bounds remain diagnostic. Default cached zero is unknown; only a validity-confirming interface can supply a genuine zero. Volume requires a confirmed session identity, not merely VolumeType.Volume.

Each expiry metric requires >=10 valid buckets and >=70% valid coverage of the returned eligible band. This is not full-exchange completeness. Known entitlement truncation suppresses concentration. Known OI reference dates must be comparable and no more than two completed, certain exchange trading dates old. Future times and stale retrievals are excluded. Unknown publication dates stay explicitly unknown.

For usable X: `strength=X/maxPositiveX`; `prominence=clamp(log2(X/medianPositiveX),0,1)`; `MetricC=.60*strength+.40*prominence`. Explicit zero gets zero; all-zero samples are unavailable. Qualifying strikes need a confirmed interior local peak in a supporting metric and raw concentration >=0.65. Equal peak plateaus collapse to the lower midpoint. Edges, missing neighboring metric values and flat samples cannot manufacture peaks; flat MetricC is 0.60.

| Mode | Raw concentration | Cap |
|---|---|---:|
| Both | .70 OI + .30 confirmed-session volume | 1.00 |
| OI only | OI MetricC | .80 |
| Volume only (activity) | Volume MetricC | .50 |
| Neither | Unmeasured context | 0 |

Unknown OI publication and coverage below 90% each impose an additional .80 cap, combined by minimum. Expiry weights are 1 up to 7 actual UTC days, .90 over 7–30 days, .80 thereafter. `effectiveC=expiryWeight*min(rawC,applicableCaps)`. Aggregate expiries at a strike by maximum, never sum. Rank distinct strikes by effective, raw, distance, lower tick, stable ID. Strong farther concentrations outrank weak nearby strikes. Valid flat/no-peak data does not fill slots with nearest strikes. If no metric is usable, optional two-per-side nearest strikes are inactive context only.

## Views, settings and export

Absolute tiers: A Critical >=75, B Major >=50, C Secondary >=35, D Minor otherwise. Default widths 4/3/2/1 px, green BUY, orange/red SELL, gray watch. Developing remains dashed. Labels prioritize stronger zones; narrow fill shows actual bounds. Six active zones per side plus three watch zones are default rendering limits, with minimum tier/score and optional components. Limits do not remove scored zones or cached observations.

`CurrentSnapshot` and `CurrentScoredSnapshot` publish atomically through one adapter view and share a source version. Scored known-at includes computation and context availability. If the viewport ends earlier, strength/zone styling is hidden and individually available raw rays may remain. This is a current snapshot, not a reconstructed historical score archive. Foreign host charts keep unconverted NQ prices and their own scale.

`ScoringSettings` and `OptionConcentrationSettings` contain versioned expert constants. Defaults above are engineering choices, not optimized thresholds. Component maxima and tier thresholds are configurable; totals still clamp to 100. Scoring settings hashes identify changed hypotheses. Legacy input names migrate explicitly and preserve stored counts; fresh instances use 8 scanned series and 3 selected strikes per side.

`CurrentScoredSnapshot.ToDiagnosticJson()` exports source/scoring versions, as-of/known-at, settings hash, actual prices, component explanations, evidence IDs, permissions, provider status, raw contracts and per-expiry metrics/coverage. Nested collections are copied/read-only; no account settings or credentials are accessed. See [scoring-example.json](scoring-example.json) for a small synthetic excerpt, not live data.

## Installed data limitations

Quantower 1.146.18 exposes cached Symbol.OpenInterest/Volume and NewLast/NewQuote/NewDayBar events. An option symbol returned by GetStrikes keeps default fields until the feed delivers data for it. An unsubscribed scan therefore reads default zero OI everywhere, which correctly yields 0% returned-bucket coverage and no ranked strikes.

Each retrieval now attaches day-bar, quote and trade handlers to the contracts inside the analysis band, nearest expiry first, in **batches of 128**. A batch is released when every member has delivered positive OI, or after 3 seconds, before the next batch opens. The whole scan shares the 90-second request deadline. Contracts whose batch never opened keep their retrieval-time reading. Only handlers the request attached are removed. The options status reports subscribed, delivered, without-OI and unattempted counts.

Field evidence stays conservative. Local metadata and official docs do not establish field validity, OI publication date or volume session identity. Delivered positive OI is a cached value with unknown publication, so it stays capped. A delivered zero stays unknown, so a bucket with a zero-OI side is not sampled. Cached volume remains diagnostic. Runtime population/entitlement checks remain outstanding.

Primary references checked 12 October 2026: [Quantower options example](https://github.com/Quantower/Examples/blob/master/IndicatorExamples/GetOptionsInfo.cs), [Symbol API](https://api.quantower.com/docs/TradingPlatform.BusinessLayer.Symbol.html), [volume-analysis guide](https://help.quantower.com/quantower/quantower-algo/access-volume-analysis-data-from-indicators), [CME weekly options](https://www.cmegroup.com/trading/equity-index/e-mini-nasdaq-100-weekly-options.html), [CME quarterly underlier FAQ](https://www.cmegroup.com/education/articles-and-reports/e-mini-nasdaq-100-monday-and-wednesday-weekly-options-frequently-asked-questions), and [OI heatmap](https://www.cmegroup.com/tools-information/quikstrike/open-interest-heatmap.html). Installed signatures are authoritative for this terminal. The scoring weights and caps above are implementation hypotheses, not exchange recommendations.
