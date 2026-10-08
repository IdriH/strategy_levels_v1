using TradingPlatform.BusinessLayer;
using NqCandidateLevels.Core;

namespace NqCandidateLevels;

internal sealed record ResolvedSource(Symbol Symbol, SourceIdentity Identity, Connection Connection);

/// <summary>Host chart symbols are deliberately absent from this API.</summary>
internal static class SourceResolver
{
    public static ResolvedSource Resolve(string connectionId, string contractId, CancellationToken cancellation)
    {
        var connections = TradingPlatform.BusinessLayer.Core.Instance.Connections.All
            .Where(c => c.VendorName.Contains("Rithmic", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var selected = string.IsNullOrWhiteSpace(connectionId)
            ? connections.Where(c => c.State == ConnectionState.Connected).ToArray()
            : connections.Where(c => c.Id == connectionId).ToArray();
        if (selected.Length != 1)
            throw new InvalidOperationException($"Select a Rithmic connection ID. Matching active connections: {string.Join(", ", connections.Select(c => $"{c.Name} [{c.Id}] {c.State}"))}");
        var connection = selected[0];
        if (connection.State != ConnectionState.Connected)
            throw new InvalidOperationException($"Rithmic {connection.Name} [{connection.Id}] is {connection.State}.");
        cancellation.ThrowIfCancellationRequested();
        Symbol[] symbols;
        if (!string.IsNullOrWhiteSpace(contractId))
        {
            var symbol = TradingPlatform.BusinessLayer.Core.Instance.GetSymbol(
                new GetSymbolRequestParameters { SymbolId = contractId, CancellationToken = cancellation }, connection.Id);
            symbols = symbol == null ? [] : [symbol];
        }
        else symbols = TradingPlatform.BusinessLayer.Core.Instance.Symbols
            .Where(s => s.ConnectionId == connection.Id && Validate(s, connection.Id, out _)).ToArray();
        if (symbols.Length != 1)
            throw new InvalidOperationException($"NQZ6 resolution returned {symbols.Length} verified contracts on {connection.Name}. Load NQZ6 in symbol lookup or set its exact contract ID; no rollover/alias is used.");
        var source = symbols[0];
        if (!Validate(source, connection.Id, out var reason)) throw new InvalidOperationException(reason);
        if (DateTime.UtcNow >= SessionCalendar.NqZ6LastTradingUtc)
            throw new InvalidOperationException("NQZ6 expired at 2026-12-18 09:30 New York; automatic rollover is disabled.");
        var identity = new SourceIdentity(source.ConnectionId, source.Id, source.Name,
            SessionCalendar.NqZ6LastTradingUtc, source.TickSize);
        TradingPlatform.BusinessLayer.Core.Instance.Loggers.Log(
            $"NQZ6 resolved name={source.Name}, id={source.Id}, connection={connection.Name} [{connection.Id}], metadata expiration={source.ExpirationDate:O}, cutoff={identity.ExpirationUtc:O}, tick={source.TickSize}", LoggingLevel.System);
        return new(source, identity, connection);
    }

    internal static bool Validate(Symbol source, string connectionId, out string reason)
    {
        var venue = source.Exchange?.ExchangeName;
        if (string.IsNullOrWhiteSpace(venue)) venue = source.ExchangeId;
        return SourceContractRules.TryValidate(new(source.ConnectionId, source.Id, source.Name, source.Root, venue,
            source.SymbolType == SymbolType.Futures,
            source.FutureContractType is FutureContractType.Continuous or FutureContractType.Spread,
            source.ExpirationDate, source.MaturityDate, source.TickSize), connectionId, out reason);
    }
}
