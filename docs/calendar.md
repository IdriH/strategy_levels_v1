# NQZ6 sessions and calendar overrides

The calendar stores UTC half-open intervals `[start, end)`. Every source bar is associated with the exchange reference date of its futures session; computer-local dates, the host chart, and Europe/Rome offsets do not participate. Chicago `17:00` on the prior civil day starts an ordinary Monday–Friday reference session. Thus Sunday evening belongs to Monday. Friday's session ends Friday afternoon; Saturday and Sunday before the evening open retain Friday as the most recent reference date and are closed. A maintenance break retains the completed reference date until `17:00`.

Ordinary 2026 NQ Globex sessions run from Chicago `17:00` on the preceding day to `16:00` on the reference date, with the `16:00–17:00` maintenance closure. The old `15:15–15:30` equity-index halt was removed effective June 28, 2021 by [CME SER-8788, section 5](https://www.cmegroup.com/content/dam/cmegroup/notices/ser/2021/06/SER-8788.pdf). It is intentionally absent from the default calendar. The [CME Nasdaq options FAQ](https://www.cmegroup.com/articles/faqs/e-mini-nasdaq-100-tuesday-and-thursday-options-frequently-asked-questions.html) confirms the current Sunday–Friday trading band and daily maintenance period; the indicator uses only NQ futures observations.

Cash/RTH is the Nasdaq cash session, New York `09:30–16:00`, as documented by [Nasdaq's hours and 2026 holiday schedule](https://www.nasdaq.com/market-activity/stock-market-holiday-schedule). This differs from some futures-provider uses of the label RTH. Overnight begins at the source futures-session start and ends at the associated cash open. It develops before that open, then freezes. A cash-closed date has no cash/overnight interval. On NQZ6's final date the overnight ends at contract termination and no NQZ6 cash interval exists. Week references use exchange Monday–Friday dates and include Sunday evening; missing holiday schedules remain uncertain rather than being interpreted as missing weekend data.

Windows and IANA timezone IDs are resolved through `TimeZoneInfo` using `America/Chicago` / `Central Standard Time` and `America/New_York` / `Eastern Standard Time`. The OS rules convert each local boundary independently; there are no fixed UTC offsets. Overrides at ambiguous/nonexistent DST-local times are rejected. For example the Monday open following the March 8, 2026 US DST change is Sunday `22:00Z`; following November 1 it is Sunday `23:00Z`.

## Certainty and the supported date range

The built-in normal calendar covers ordinary 2026 weekdays through NQZ6 termination. Dates before 2026 need an override-capable calendar revision before they can produce certain references. Dates after termination are closed for this contract. No rollover occurs.

The Nasdaq cash closures and published early closes are known, but they do not establish NQ's Globex sessions or the exchange's merged holiday trade dates. [CME's current holiday schedule](https://www.cmegroup.com/trading-hours.html) says product holiday schedules are subject to change and are usually finalized around two weeks before the holiday. The dynamic product timetable was not available as a reliable machine-readable NQ schedule during implementation. No holiday times or Rithmic assignments are inferred from a cash-market holiday, an old brochure, or settlement/clearing notices.

These conservative **affected reference dates** are marked uncertain until a local verified override is supplied:

| Period | 2026 dates needing verification |
| --- | --- |
| New Year | Jan 1–2 |
| MLK Day | Jan 19–20 |
| Presidents Day | Feb 16–17 |
| Good Friday | Apr 2–3, Apr 6 |
| Memorial Day | May 25–26 |
| Juneteenth | Jun 18–19, Jun 22 |
| Independence Day | Jul 2–3, Jul 6 |
| Labor Day | Sep 7–8 |
| Thanksgiving | Nov 25–27 |
| Christmas / New Year | Dec 24–25, Dec 28, Dec 31 (after NQZ6 expiry) |

Some surrounding dates may ultimately have an ordinary schedule. The conservative set protects session-start and preceding-session calculations until those assignments have been checked. Columbus Day and Veterans Day are ordinary defaults; a clearing/settlement notice alone does not imply a trading closure. Calendar uncertainty suppresses calculations requiring the affected session. An earlier valid reference is not substituted for an uncertain expected session and relabelled as a fresh previous session. Weekly references require certainty for every applicable date.

An uncertain session keeps tentative bounds for diagnostics but `IsOpen` returns false. Health/status code must distinguish uncertainty from a verified market closure. Observations in tentative bounds do not establish a complete schedule.

## Local JSON mechanism

Set **Calendar override path** to a readable JSON file, then reinitialize the indicator after editing the file. Loading happens once per engine initialization and is bounded to 1 MiB, 400 dates and 32 intervals per date. A missing file, malformed JSON, unknown property, inconsistent closure, duplicate date, invalid boundary, or overlap causes the requested calendar to fail closed with its error in status. It never silently falls back to certain ordinary sessions after an override-loading failure.

[The supplied example](../calendars/nqz6-2026.example.json) contains the verified expiration window only. It does **not** resolve holiday uncertainty. Copy it and add product schedules checked against CME's NQ Globex timetable. `verified: true` is the operator's explicit assertion that the supplied schedule and reference-date assignment were checked; the program does not fetch or authenticate the source URL. Keep the URL/document name and check date in `source` so the assertion can be reviewed. An unverified entry remains uncertain even if complete intervals are supplied.

Each entry requires:

| Field | Meaning |
| --- | --- |
| `tradingDate` | Exchange reference date, `yyyy-MM-dd`, within 2026 |
| `verified` | Whether the NQ schedule and date assignment have actually been verified |
| `source` | Authoritative source and review date; required for verified entries |
| `reason` | Optional explanation shown for an unverified entry |
| `futuresClosed` | Explicit boolean; true requires an empty futures interval list |
| `futuresIntervalsChicago` | Every tradable interval, in order or sortable, with `start`/`end` local Chicago timestamps `yyyy-MM-ddTHH:mm:ss` |
| `cashClosed` | Explicit boolean describing the usable NQ cash-reference window; true requires `cashNewYork: null` |
| `cashNewYork` | One `start`/`end` New York local cash window on the reference date, or null |

Use absolute exchange-local civil timestamps, without `Z` or an offset. A shortened session supplies its actual earlier end and, if applicable, the actual early cash close. Explicitly list separate futures intervals around every verified maintenance period/halt; those gaps are excluded from coverage and aggregation. Cash and overnight windows describe bounds, so their observations and coverage must also intersect the tradable futures intervals.

For a merged holiday trade date, assign all verified futures intervals to the actual exchange reference date and explicitly close/remap all neighboring reference dates that would otherwise overlap. For example, when exchange metadata says Sunday-night and Monday trading belong to Tuesday, Tuesday's entry includes those intervals and Monday's entry has `futuresClosed: true`, empty intervals, and no cash window. This is an explanation of the schema, not a claim about any particular 2026 holiday. `GetTradingDate` prefers explicit override windows, including their internal closed gaps, over the ordinary `17:00` rule. Overlapping dates are rejected to prevent double counting. No interval can extend beyond its reference civil date or span more than seven preceding dates.

## Contract termination

[CME rule 35902.G and 35903.A](https://www.cmegroup.com/content/dam/cmegroup/rulebook/CME/IV/350/359/359.pdf) terminate quarterly NQ trading at the Nasdaq regular opening on the final-settlement business day, normally the third Friday. December 2026's third Friday is **December 18, 2026**, so the built-in cutoff is New York `09:30`, Chicago `08:30`, **14:30 UTC**. The final futures/overnight interval is clipped to this cutoff; later active candidates are suppressed. A date-only/midnight provider expiration does not supersede this trading cutoff. An unscheduled exchange holiday or emergency requires updated verified exchange evidence and a calendar/code revision; a JSON override cannot extend NQZ6 beyond the built-in termination.

The SDK adapter has not established provider schedule metadata for this Rithmic session. The calendar is a documented local mechanism; it does not claim live schedule synchronization, automatic emergency-halt discovery, or retrospective chain/replay support.
