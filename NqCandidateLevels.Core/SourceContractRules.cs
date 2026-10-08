using System.Text.RegularExpressions;

namespace NqCandidateLevels.Core;

/// <summary>Only source-contract metadata; host-chart identity and prices have no place in resolution.</summary>
public sealed record SourceContractMetadata(
    string ConnectionId,
    string ContractId,
    string Name,
    string? Root,
    string? Venue,
    bool IsFutures,
    bool IsContinuousOrSpread,
    DateTime ExpirationDate,
    DateTime MaturityDate,
    double TickSize);

public static class SourceContractRules
{
    private static readonly Regex ExactContractName = new(
        @"^NQZ(?:6|26)(?:[.@:]CME)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// Validates metadata identifying the fixed December 2026 E-mini Nasdaq futures contract.
    /// Actual expiration/connection health is a separate adapter check with an explicit clock.
    /// </summary>
    public static bool TryValidate(SourceContractMetadata metadata, string selectedConnectionId, out string reason)
    {
        reason = "";
        if (metadata is null)
            reason = "Source contract metadata is unavailable.";
        else if (string.IsNullOrWhiteSpace(selectedConnectionId) ||
                 !string.Equals(metadata.ConnectionId, selectedConnectionId, StringComparison.Ordinal))
            reason = "Source belongs to another connection or its connection is unavailable.";
        else if (!metadata.IsFutures)
            reason = "Source is not a futures contract.";
        else if (metadata.IsContinuousOrSpread)
            reason = "Continuous futures and spreads are prohibited.";
        else if (string.IsNullOrWhiteSpace(metadata.Name) || !ExactContractName.IsMatch(metadata.Name))
            reason = "Source name does not identify NQZ6/NQZ26.";
        else if (!string.IsNullOrWhiteSpace(metadata.Root) &&
                 !string.Equals(metadata.Root, "NQ", StringComparison.OrdinalIgnoreCase))
            reason = "Source root is not NQ.";
        else if (!IsVerifiedVenue(metadata.Venue))
            reason = "CME venue cannot be verified; exact CME or CME Globex metadata is required.";
        else if (metadata.ExpirationDate.Year != 2026 || metadata.ExpirationDate.Month != 12)
            reason = "December 2026 expiration metadata is required.";
        else if (metadata.MaturityDate != default &&
                 (metadata.MaturityDate.Year != 2026 || metadata.MaturityDate.Month != 12))
            reason = "Maturity metadata identifies another contract month.";
        else if (!double.IsFinite(metadata.TickSize) || Math.Abs(metadata.TickSize - 0.25) > 1e-9)
            reason = "NQ 0.25-point tick size cannot be verified.";
        else if (string.IsNullOrWhiteSpace(metadata.ContractId))
            reason = "Source contract ID is unavailable.";
        return reason.Length == 0;
    }

    private static bool IsVerifiedVenue(string? venue)
    {
        var normalized = venue?.Trim();
        return string.Equals(normalized, "CME", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "CME Globex", StringComparison.OrdinalIgnoreCase);
    }
}
