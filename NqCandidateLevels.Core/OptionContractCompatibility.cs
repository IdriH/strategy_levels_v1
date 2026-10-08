namespace NqCandidateLevels.Core;

/// <summary>Evidence established by the SDK adapter for one populated underlying field.
/// MatchesSource must identify the exact validated futures contract on its connection.
/// A product root or a similar display name is never enough.</summary>
public enum OptionUnderlyingEvidence { Missing, MatchesSource, Unresolved, ConflictsWithSource }

/// <summary>The adapter retains the original SDK calendar date and whether it supplies a time.
/// Date-only values do not establish an end-of-day expiration time.</summary>
public sealed record OptionExpiryMetadata(DateTime Utc, bool IsDateOnly = false, DateOnly? CalendarDate = null)
{
    public DateOnly Date => CalendarDate ?? DateOnly.FromDateTime(Utc);
}

public sealed record OptionSeriesCompatibilityMetadata
{
    public string SeriesId { get; init; } = "";
    public string ConnectionId { get; init; } = "";
    public OptionUnderlyingEvidence Underlying { get; init; }
    public OptionExpiryMetadata? Expiry { get; init; }
}

public sealed record OptionCompatibilityMetadata
{
    public string ContractId { get; init; } = "";
    public string ConnectionId { get; init; } = "";
    public bool IsOptionSymbol { get; init; }
    public double Strike { get; init; }
    public OptionRight Right { get; init; } = OptionRight.Unknown;
    public OptionExpiryMetadata? Expiry { get; init; }
    public OptionUnderlyingEvidence UnderlyingId { get; init; }
    public OptionUnderlyingEvidence UnderlyingObject { get; init; }
    public OptionSeriesCompatibilityMetadata? DeclaredSeries { get; init; }
    /// <summary>Set only for symbols returned by the SDK GetStrikes request for the independently
    /// verified requested series. This supplies absent optional metadata, never contradictory data.</summary>
    public bool ReturnedByRequestedSeries { get; init; }
}

[Flags]
public enum OptionCompatibilityRejection
{
    None = 0,
    MissingContractId = 1 << 0,
    WrongConnection = 1 << 1,
    WrongSymbolType = 1 << 2,
    InvalidStrike = 1 << 3,
    UnknownOptionRight = 1 << 4,
    InvalidExpiry = 1 << 5,
    Expired = 1 << 6,
    ExpiryMismatch = 1 << 7,
    UnverifiedRequestSeries = 1 << 8,
    UnderlyingIdUnresolved = 1 << 9,
    UnderlyingIdConflict = 1 << 10,
    UnderlyingObjectUnresolved = 1 << 11,
    UnderlyingObjectConflict = 1 << 12,
    MissingUnderlyingEvidence = 1 << 13,
    DeclaredSeriesMismatch = 1 << 14,
    DeclaredSeriesConnectionMismatch = 1 << 15,
    DeclaredSeriesUnderlyingUnresolved = 1 << 16,
    DeclaredSeriesUnderlyingConflict = 1 << 17,
    DeclaredSeriesExpiryMismatch = 1 << 18,
    MissingSeriesEvidence = 1 << 19
}

public sealed record OptionCompatibilityResult(OptionCompatibilityRejection Rejections, DateTime? ExpiryUtc,
    bool UsedRequestUnderlying, bool UsedSeriesExpiry)
{
    public bool Accepted => Rejections == OptionCompatibilityRejection.None;
    public IEnumerable<string> Reasons => OptionContractCompatibility.ReasonLabels(Rejections);
}

/// <summary>Pure validation of normalized SDK options metadata. Missing leaf fields may inherit
/// verified GetStrikes series context; supplied conflicts and unresolved aliases fail closed.</summary>
public static class OptionContractCompatibility
{
    public static OptionCompatibilityResult Validate(OptionCompatibilityMetadata option,
        OptionSeriesCompatibilityMetadata requestedSeries, SourceIdentity source, DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(requestedSeries);
        ArgumentNullException.ThrowIfNull(source);
        if (asOfUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC observation required.", nameof(asOfUtc));

        var reasons = OptionCompatibilityRejection.None;
        var requestVerified = !string.IsNullOrWhiteSpace(source.ContractId) && !string.IsNullOrWhiteSpace(source.ConnectionId) &&
            !string.IsNullOrWhiteSpace(requestedSeries.SeriesId) && requestedSeries.ConnectionId == source.ConnectionId &&
            requestedSeries.Underlying == OptionUnderlyingEvidence.MatchesSource && ValidExpiry(requestedSeries.Expiry);
        if (!requestVerified) reasons |= OptionCompatibilityRejection.UnverifiedRequestSeries;
        if (string.IsNullOrWhiteSpace(option.ContractId)) reasons |= OptionCompatibilityRejection.MissingContractId;
        if (option.ConnectionId != source.ConnectionId) reasons |= OptionCompatibilityRejection.WrongConnection;
        if (!option.IsOptionSymbol) reasons |= OptionCompatibilityRejection.WrongSymbolType;
        if (!double.IsFinite(option.Strike) || option.Strike <= 0) reasons |= OptionCompatibilityRejection.InvalidStrike;
        if (option.Right is not (OptionRight.Call or OptionRight.Put)) reasons |= OptionCompatibilityRejection.UnknownOptionRight;
        if (option.UnderlyingId == OptionUnderlyingEvidence.Unresolved) reasons |= OptionCompatibilityRejection.UnderlyingIdUnresolved;
        if (option.UnderlyingId == OptionUnderlyingEvidence.ConflictsWithSource) reasons |= OptionCompatibilityRejection.UnderlyingIdConflict;
        if (option.UnderlyingObject == OptionUnderlyingEvidence.Unresolved) reasons |= OptionCompatibilityRejection.UnderlyingObjectUnresolved;
        if (option.UnderlyingObject == OptionUnderlyingEvidence.ConflictsWithSource) reasons |= OptionCompatibilityRejection.UnderlyingObjectConflict;

        var declared = option.DeclaredSeries;
        var declaredVerified = false;
        if (declared is not null)
        {
            if (!string.IsNullOrWhiteSpace(declared.SeriesId) && declared.SeriesId != requestedSeries.SeriesId)
                reasons |= OptionCompatibilityRejection.DeclaredSeriesMismatch;
            if (!string.IsNullOrWhiteSpace(declared.ConnectionId) && declared.ConnectionId != source.ConnectionId)
                reasons |= OptionCompatibilityRejection.DeclaredSeriesConnectionMismatch;
            if (declared.Underlying == OptionUnderlyingEvidence.Unresolved)
                reasons |= OptionCompatibilityRejection.DeclaredSeriesUnderlyingUnresolved;
            if (declared.Underlying == OptionUnderlyingEvidence.ConflictsWithSource)
                reasons |= OptionCompatibilityRejection.DeclaredSeriesUnderlyingConflict;
            if (declared.Expiry is not null && !ValidExpiry(declared.Expiry)) reasons |= OptionCompatibilityRejection.InvalidExpiry;
            if (ValidExpiry(declared.Expiry) && ValidExpiry(requestedSeries.Expiry) && !CompatibleExpiry(declared.Expiry!, requestedSeries.Expiry!))
                reasons |= OptionCompatibilityRejection.DeclaredSeriesExpiryMismatch;
            declaredVerified = declared.SeriesId == requestedSeries.SeriesId && declared.ConnectionId == source.ConnectionId &&
                declared.Underlying == OptionUnderlyingEvidence.MatchesSource &&
                (declared.Expiry is null || ValidExpiry(declared.Expiry) && CompatibleExpiry(declared.Expiry, requestedSeries.Expiry!));
        }
        var membershipVerified = requestVerified && (option.ReturnedByRequestedSeries || declaredVerified);
        if (!membershipVerified) reasons |= OptionCompatibilityRejection.MissingSeriesEvidence;
        var ownUnderlyingVerified = option.UnderlyingId == OptionUnderlyingEvidence.MatchesSource ||
            option.UnderlyingObject == OptionUnderlyingEvidence.MatchesSource || declaredVerified;
        var useRequestUnderlying = !ownUnderlyingVerified && membershipVerified &&
            option.UnderlyingId == OptionUnderlyingEvidence.Missing && option.UnderlyingObject == OptionUnderlyingEvidence.Missing;
        if (!ownUnderlyingVerified && !useRequestUnderlying) reasons |= OptionCompatibilityRejection.MissingUnderlyingEvidence;

        if (option.Expiry is not null && !ValidExpiry(option.Expiry)) reasons |= OptionCompatibilityRejection.InvalidExpiry;
        if (option.Expiry is null && !membershipVerified) reasons |= OptionCompatibilityRejection.InvalidExpiry;
        if (ValidExpiry(option.Expiry) && ValidExpiry(requestedSeries.Expiry) && !CompatibleExpiry(option.Expiry!, requestedSeries.Expiry!))
            reasons |= OptionCompatibilityRejection.ExpiryMismatch;
        if (ValidExpiry(option.Expiry) && declared is not null && ValidExpiry(declared.Expiry) && !CompatibleExpiry(option.Expiry!, declared.Expiry!))
            reasons |= OptionCompatibilityRejection.DeclaredSeriesExpiryMismatch;

        // Prefer an actual supplied time from the matching series over midnight date-only metadata.
        // When all values are date-only, midnight remains the conservative known cutoff.
        var expiries = new[] { requestedSeries.Expiry, option.Expiry, declared?.Expiry }.Where(ValidExpiry).Cast<OptionExpiryMetadata>().ToArray();
        var precise = expiries.Where(x => !x.IsDateOnly).ToArray();
        DateTime? expiryUtc = precise.Length > 0 ? precise.Min(x => x.Utc) : expiries.Length > 0 ? expiries.Min(x => x.Utc) : null;
        if (!expiryUtc.HasValue) reasons |= OptionCompatibilityRejection.InvalidExpiry;
        else if (expiryUtc.Value <= asOfUtc) reasons |= OptionCompatibilityRejection.Expired;
        var useSeriesExpiry = expiryUtc.HasValue && (option.Expiry is null || option.Expiry.IsDateOnly && expiryUtc.Value != option.Expiry.Utc);
        return new(reasons, reasons == OptionCompatibilityRejection.None ? expiryUtc : null, useRequestUnderlying, useSeriesExpiry);
    }

    public static bool CompatibleExpiry(OptionExpiryMetadata first, OptionExpiryMetadata second) =>
        ValidExpiry(first) && ValidExpiry(second) && (first.Utc == second.Utc ||
            first.Date == second.Date && (first.IsDateOnly || second.IsDateOnly));

    public static bool ValidExpiry(OptionExpiryMetadata? expiry) => expiry is not null &&
        expiry.Utc.Kind == DateTimeKind.Utc && expiry.Utc.Year is >= 2020 and <= 2100 &&
        expiry.Date.Year is >= 2020 and <= 2100;

    public static IEnumerable<string> ReasonLabels(OptionCompatibilityRejection reasons)
    {
        foreach (var (flag, label) in Labels)
            if ((reasons & flag) != 0) yield return label;
    }

    private static readonly (OptionCompatibilityRejection Flag, string Label)[] Labels =
    [
        (OptionCompatibilityRejection.MissingContractId, "contract id missing"),
        (OptionCompatibilityRejection.WrongConnection, "connection mismatch"),
        (OptionCompatibilityRejection.WrongSymbolType, "symbol type"),
        (OptionCompatibilityRejection.InvalidStrike, "invalid strike"),
        (OptionCompatibilityRejection.UnknownOptionRight, "option right"),
        (OptionCompatibilityRejection.InvalidExpiry, "invalid expiry"),
        (OptionCompatibilityRejection.Expired, "expired"),
        (OptionCompatibilityRejection.ExpiryMismatch, "expiry mismatch"),
        (OptionCompatibilityRejection.UnverifiedRequestSeries, "request series unverified"),
        (OptionCompatibilityRejection.UnderlyingIdUnresolved, "underlying id unresolved"),
        (OptionCompatibilityRejection.UnderlyingIdConflict, "underlying id conflict"),
        (OptionCompatibilityRejection.UnderlyingObjectUnresolved, "underlying object unresolved"),
        (OptionCompatibilityRejection.UnderlyingObjectConflict, "underlying object conflict"),
        (OptionCompatibilityRejection.MissingUnderlyingEvidence, "underlying unverified"),
        (OptionCompatibilityRejection.DeclaredSeriesMismatch, "series id mismatch"),
        (OptionCompatibilityRejection.DeclaredSeriesConnectionMismatch, "series connection mismatch"),
        (OptionCompatibilityRejection.DeclaredSeriesUnderlyingUnresolved, "series underlying unresolved"),
        (OptionCompatibilityRejection.DeclaredSeriesUnderlyingConflict, "series underlying conflict"),
        (OptionCompatibilityRejection.DeclaredSeriesExpiryMismatch, "series expiry mismatch"),
        (OptionCompatibilityRejection.MissingSeriesEvidence, "series membership unverified")
    ];
}
