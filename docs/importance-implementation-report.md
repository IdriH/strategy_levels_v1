# importance-v1 implementation report

Implemented and validated locally on 12 October 2026 against checkout `077f5fe6e09fdd159e604c79dd4c93e24e87930a`, Quantower SDK **1.146.18.0**, .NET SDK **10.0.401**, Windows. No applicable AGENTS.md was found in the checkout or its ancestor directories. Project/assembly identity and build/install scripts are unchanged. No deployment, market-data purchase or order operation was performed.

## Implemented files

- Core `ScoringModels.cs`, `LevelScoring.cs`, `LevelClustering.cs`: typed immutable zones, component explanations, deterministic IDs/order, event deduplication, version/settings hash, ranked lists and diagnostic JSON.
- Core `Models.cs`, `LevelEngine.cs`: typed reference kinds and actual originating-event keys, preserved raw snapshot, independent evidence validity and copied optional metadata.
- Core `OptionConcentration.cs`, `OptionUniverseSelection.cs`, `OptionSelection.cs`: bounded horizon-aware universe, canonical expiry/strike buckets, field availability/session/publication evidence, coverage and peak tests, mode/expiry caps, retained full diagnostics and cheap cached reselection. Legacy nearest selector remains explicitly separate.
- Core `ProfileGeometry.cs`: owned immutable validated histogram, actual smoothing/median, POC/HVN prominence and independently identified LVN flanks; original detection/ties and conservation remain.
- Adapter `OptionalProviders.cs`, `SourceDataAdapter.cs`: shared eight-series/probe budget, all verified rows retained, source-price band, normalized analysis cache, explicit wall-clock TTL/expiry invalidation, typed optional references and atomic raw/scored publication.
- UI `NqZ6CandidateLevelsIndicator.cs`, `CandidateRenderer.cs`: public CurrentScoredSnapshot, saved-setting name migration, weights/tiers/filter controls, ranked buy/sell/watch table, tier widths, actual bounds/anchors, prioritized labels, mode/stale badges and viewport known-at gating.
- Tests `ScoringChecks.cs`, `OptionConcentrationChecks.cs`, registration in `Program.cs`; README, scoring guide/example, SDK evidence and chart checklist updated. `.gitignore` was converted from malformed UTF-16/literal-newline content to ordinary UTF-8 patterns and now excludes new local build outputs. Pre-existing tracked build artifacts were preserved/restored separately.

## Defaults and formulas

`clamp(Base + Confluence + Options + Profile,0,100)` with maxima 40/25/25/10. Base uses the strongest valid member; developing multiplier .75. Confluence uses distinct event-deduplicated families for 0/10/18/25 points. Options uses maximum capped effective concentration; profile uses maximum validated prominence. No distance, round-number, touch-count or inferred dealer bonuses. Default bounded zones span 8 ticks; tier cutoffs 75/50/35; six rendered zones per side.

Options default to 8 scanned series, 3 selected measured strikes per side, 120-second metadata refresh, 300-second TTL, >=10 valid buckets and >=70% returned coverage. Band half-width is max(500,2*previous completed RTH range), capped at 2,000 points. Strength/prominence mix .60/.40; both-metric mix .70/.30. OI-only/volume-only caps .80/.50, unknown publication or <90% coverage cap .80, expiry relevance 1/.90/.80. Missing evidence never redistributes weights. [Full rules](scoring.md) include base weights, peak criteria and all limitations.

## Validation performed

```powershell
dotnet build .\candidate_levels_v1_1.slnx -c Release
dotnet run --project .\NqCandidateLevels.Tests\NqCandidateLevels.Tests.csproj -c Release
```

Final result: **build succeeded, 0 warnings, 0 errors; 25/25 test groups passed**. The original 23 groups remain unchanged in meaning and pass. The two new groups contain multiple independent assertions covering:

- Expected **84/A** components (38+18+20+8), alias **38/C**, exact tier boundaries, fixed missing-component denominator, developing discount, weight-zero behavior and total clamp.
- No transitive clustering, exact tick merge, actual anchors, shuffled determinism, four biases, straddling watch context, source inactivity and unavailable evidence.
- Future/reference/metric availability, newly computed known-at, earliest normalization TTL, immutable nested copies and JSON serialization.
- Farther stronger strikes, duplicate calls/puts and conflicting IDs, per-expiry normalization, three-expiry ranking, mode/publication/coverage/expiry caps, unknown versus explicit zero, unconfirmed volume, sparse/partial/truncated/flat/all-zero samples, mixed/future/stale reference dates.
- Plateau midpoint, edge/hole refusal, duplicate/conflicting series, bounded same-day probes and near/far universe coverage.
- Real profile prominence, flat POC zero, genuine interior LVN flanks and missing-flank refusal; existing native volume-conservation checks still pass.

Local metadata-only inspection reconfirmed option metric/event signatures without initializing Core or requesting feed data. Source review checked native coordinates, viewport suppression, cancellation/epoch guards, existing handler removal, source-only calculations and absence of requests/scoring in paint. An audit fixed wall-clock expiry even when source timestamps freeze and removed a quadratic concentration lookup. Histograms are immutable and shared rather than repeatedly copied per profile node.

## Limitations and remaining live checks

**No live Quantower/Rithmic acceptance tests were run.** Compilation certifies the installed signatures, not feed delivery, entitlement completeness, migration of a real saved workspace or runtime rendering/lifecycle behavior. Follow [chart-validation.md](chart-validation.md) on NQ and foreign-host charts, including dense labels, stale/disconnect/reconnect, corrections, all biases, saved settings and repeated removal.

OI publication/validity and volume-session identity are not established by the installed cached fields. Positive cached OI remains conservatively capped/unknown; cached volume remains diagnostic. Repeated metadata retrieval is not evidence of field refresh. This release adds no option subscriptions: neither inspected metadata nor the documented quote example establishes that they guarantee OI or are necessary for this provider. If live evidence establishes a subscription requirement, a bounded entire-scan lifecycle must be designed and checked before claiming delivery.

Coverage is of returned eligible rows, not a proof of exchange-chain completeness. Explicit known truncation is refused; unadvertised provider truncation cannot be detected from these SDK signatures. Native profile permission/coverage refusal remains authoritative. The scored view is current-only and does not reconstruct a historical archive.

## Installation

Build output remains local in `candidate_levels_v1_1/bin/Release`. To deploy deliberately after review, run the unchanged installer:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-Indicator.ps1 -InstallationRoot C:\Quantower
```

Remove the old indicator, restart Quantower to reload both assemblies, and add **NQZ6 Candidate Levels**. Confirm the source/connection and run the live checklist. The implementation session did not run this installer.
