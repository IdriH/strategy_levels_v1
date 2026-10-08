using NqCandidateLevels.Core;

internal static class OptionsCompatibilityChecks
{
    public static void Run()
    {
        var source = new SourceIdentity("rithmic-1", "exact-NQZ6", "NQZ6", SessionCalendar.NqZ6LastTradingUtc, 0.25);
        var observation = Utc(8, 16);
        var expiry = new OptionExpiryMetadata(Utc(9, 20));
        var series = new OptionSeriesCompatibilityMetadata
        {
            SeriesId = "weekly-20261009", ConnectionId = source.ConnectionId,
            Underlying = OptionUnderlyingEvidence.MatchesSource, Expiry = expiry
        };
        var option = new OptionCompatibilityMetadata
        {
            ContractId = "weekly-call-25000", ConnectionId = source.ConnectionId, IsOptionSymbol = true,
            Strike = 25000, Right = OptionRight.Call, Expiry = expiry,
            UnderlyingId = OptionUnderlyingEvidence.MatchesSource, ReturnedByRequestedSeries = true
        };
        OptionCompatibilityResult Verify(OptionCompatibilityMetadata row, OptionSeriesCompatibilityMetadata? request = null,
            DateTime? at = null) => OptionContractCompatibility.Validate(row, request ?? series, source, at ?? observation);
        void Reject(OptionCompatibilityMetadata row, OptionCompatibilityRejection reason, string message,
            OptionSeriesCompatibilityMetadata? request = null)
        {
            var result = Verify(row, request);
            Check(!result.Accepted && (result.Rejections & reason) != 0 && !result.ExpiryUtc.HasValue, message);
        }

        var exact = Verify(option);
        Check(exact.Accepted && exact.ExpiryUtc == expiry.Utc, "Exact source/series metadata must verify");
        Check(exact.ExpiryUtc < source.ExpirationUtc, "October weekly expiry on December future remains valid");
        var midnight = new OptionExpiryMetadata(Utc(9), true);
        var dateOnly = Verify(option with { Expiry = midnight });
        Check(dateOnly.Accepted && dateOnly.ExpiryUtc == expiry.Utc && dateOnly.UsedSeriesExpiry,
            "Same declared series date-only leaf metadata uses supplied precise series time");
        Check(Verify(option with { Expiry = new(Utc(8, 22), true, new DateOnly(2026, 10, 9)) }).Accepted,
            "Original date-only calendar date survives UTC conversion across midnight");
        Check(Verify(option, series with { Expiry = midnight }).Accepted,
            "Precise leaf expiration is compatible with same-date date-only request metadata");
        Reject(option with { Expiry = new(Utc(9, 21)) }, OptionCompatibilityRejection.ExpiryMismatch,
            "Distinct supplied precise times are not silently reconciled");
        Reject(option with { Expiry = new(Utc(10), true) }, OptionCompatibilityRejection.ExpiryMismatch,
            "Date-only values on different dates are incompatible");
        Check(!OptionContractCompatibility.CompatibleExpiry(new(Utc(9, 20)), new(Utc(9, 21))),
            "Same calendar date alone must not reconcile precise expirations");
        Check(!OptionContractCompatibility.CompatibleExpiry(new(Utc(9)), expiry),
            "Midnight must be explicitly identified as date-only before relaxing timestamp comparison");
        Check(Verify(option with { Expiry = null }).Accepted,
            "Missing optional leaf expiry inherits exact verified request-series metadata");
        Reject(option with { Expiry = new(DateTime.MinValue) }, OptionCompatibilityRejection.InvalidExpiry,
            "Present invalid leaf expiration cannot be repaired by request context");
        Reject(option with { Expiry = new(DateTime.SpecifyKind(expiry.Utc, DateTimeKind.Unspecified)) },
            OptionCompatibilityRejection.InvalidExpiry, "Policy requires adapter-normalized UTC dates");

        var sameDay = series with { Expiry = new(Utc(8, 20)) };
        Check(Verify(option with { Expiry = new(Utc(8), true) }, sameDay).Accepted,
            "Date-only option on current day can use actual supplied still-future series time");
        var unknownCutoff = sameDay with { Expiry = new(Utc(8), true) };
        Reject(option with { Expiry = new(Utc(8), true) }, OptionCompatibilityRejection.Expired,
            "Date-only metadata never fabricates an end-of-day cutoff", unknownCutoff);
        Reject(option with { Expiry = new(Utc(8, 15)) }, OptionCompatibilityRejection.Expired,
            "Known expired metadata remains excluded", series with { Expiry = new(Utc(8, 15)) });

        var missing = option with { UnderlyingId = OptionUnderlyingEvidence.Missing };
        var inherited = Verify(missing);
        Check(inherited.Accepted && inherited.UsedRequestUnderlying,
            "Exact GetStrikes series provenance supplies absent optional underlying fields");
        Reject(missing with { ReturnedByRequestedSeries = false }, OptionCompatibilityRejection.MissingUnderlyingEvidence,
            "Absent leaf metadata needs independently verified request membership");
        Reject(option with { ReturnedByRequestedSeries = false }, OptionCompatibilityRejection.MissingSeriesEvidence,
            "Direct underlying metadata alone does not prove requested series membership");
        Reject(option with { UnderlyingId = OptionUnderlyingEvidence.Unresolved }, OptionCompatibilityRejection.UnderlyingIdUnresolved,
            "A populated root-only or unresolved alias cannot be overridden by request provenance");
        Reject(option with { UnderlyingObject = OptionUnderlyingEvidence.ConflictsWithSource },
            OptionCompatibilityRejection.UnderlyingObjectConflict,
            "Matching underlying ID cannot override object identifying another futures contract");
        Reject(option with { UnderlyingId = OptionUnderlyingEvidence.ConflictsWithSource,
            UnderlyingObject = OptionUnderlyingEvidence.MatchesSource }, OptionCompatibilityRejection.UnderlyingIdConflict,
            "Matching object cannot override conflicting populated underlying ID");
        Reject(option with { UnderlyingObject = OptionUnderlyingEvidence.Unresolved },
            OptionCompatibilityRejection.UnderlyingObjectUnresolved, "Populated unresolved underlying object is diagnostic");
        Check(Verify(option with { UnderlyingId = OptionUnderlyingEvidence.MatchesSource,
            UnderlyingObject = OptionUnderlyingEvidence.MatchesSource }).Accepted,
            "Adapter-resolved exact futures aliases can match both populated metadata fields");

        Check(Verify(missing with { DeclaredSeries = new() }).Accepted,
            "Empty optional declared-series placeholder does not contradict exact request provenance");
        Check(Verify(missing with { ReturnedByRequestedSeries = false, DeclaredSeries = series }).Accepted,
            "Exact populated declared series independently supplies series/underlying evidence");
        Reject(option with { DeclaredSeries = series with { SeriesId = "different-weekly-same-date" } },
            OptionCompatibilityRejection.DeclaredSeriesMismatch,
            "Different series on identical expiry stay distinct");
        Reject(option with { DeclaredSeries = series with { ConnectionId = "another-rithmic-login" } },
            OptionCompatibilityRejection.DeclaredSeriesConnectionMismatch,
            "Declared series from another connection is excluded");
        Reject(option with { DeclaredSeries = series with { Underlying = OptionUnderlyingEvidence.ConflictsWithSource } },
            OptionCompatibilityRejection.DeclaredSeriesUnderlyingConflict,
            "Declared series for another future is excluded");
        Reject(option with { DeclaredSeries = series with { Underlying = OptionUnderlyingEvidence.Unresolved } },
            OptionCompatibilityRejection.DeclaredSeriesUnderlyingUnresolved,
            "Populated unverifiable series root remains excluded");
        Reject(option with { DeclaredSeries = series with { Expiry = new(Utc(9, 21)) } },
            OptionCompatibilityRejection.DeclaredSeriesExpiryMismatch,
            "Declared series precise expiration conflict is reported");
        Reject(option, OptionCompatibilityRejection.UnverifiedRequestSeries,
            "Request series itself needs exact underlying evidence", series with { Underlying = OptionUnderlyingEvidence.Unresolved });
        Reject(option, OptionCompatibilityRejection.UnverifiedRequestSeries,
            "Request series itself needs same source connection", series with { ConnectionId = "other" });

        Reject(option with { ContractId = "" }, OptionCompatibilityRejection.MissingContractId, "Missing contract IDs reported");
        Reject(option with { ConnectionId = "other" }, OptionCompatibilityRejection.WrongConnection, "Wrong contract connection reported");
        Reject(option with { IsOptionSymbol = false }, OptionCompatibilityRejection.WrongSymbolType, "Wrong symbol type reported");
        Reject(option with { Strike = double.NaN }, OptionCompatibilityRejection.InvalidStrike, "Non-finite strike reported");
        Reject(option with { Strike = 0 }, OptionCompatibilityRejection.InvalidStrike, "Non-positive strike reported");
        Reject(option with { Right = OptionRight.Unknown }, OptionCompatibilityRejection.UnknownOptionRight, "Unknown option right reported");
        var multiple = Verify(option with { IsOptionSymbol = false, Right = OptionRight.Unknown, UnderlyingId = OptionUnderlyingEvidence.Unresolved });
        Check(multiple.Reasons.Contains("symbol type") && multiple.Reasons.Contains("option right") &&
            multiple.Reasons.Contains("underlying id unresolved"), "Rejected rows report all applicable granular causes");
        Check(!exact.Reasons.Any(), "Accepted rows have no rejection reasons");
    }

    private static DateTime Utc(int day, int hour = 0) => new(2026, 10, day, hour, 0, 0, DateTimeKind.Utc);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
