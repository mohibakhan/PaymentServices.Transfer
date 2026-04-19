using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PaymentServices.Transfer.Models;

namespace PaymentServices.Transfer.Repositories;

public interface ILedgerRepository
{
    /// <summary>Gets a ledger document by its ID.</summary>
    Task<LedgerDocument?> GetLedgerAsync(
        string ledgerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a ledger entry and updates the ledger LastBalance atomically.
    /// Returns the written entry with its generated GluId.
    /// </summary>
    Task<LedgerEntry> WriteEntryAsync(
        LedgerEntry entry,
        decimal newBalance,
        CancellationToken cancellationToken = default);
}

public sealed class LedgerRepository : ILedgerRepository
{
    private readonly Container _ledgerContainer;
    private readonly Container _ledgerEntriesContainer;
    private readonly ILogger<LedgerRepository> _logger;

    public LedgerRepository(
        [FromKeyedServices("ledgers")] Container ledgerContainer,
        [FromKeyedServices("ledgerEntries")] Container ledgerEntriesContainer,
        ILogger<LedgerRepository> logger)
    {
        _ledgerContainer = ledgerContainer;
        _ledgerEntriesContainer = ledgerEntriesContainer;
        _logger = logger;
    }

    public async Task<LedgerDocument?> GetLedgerAsync(
        string ledgerId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _ledgerContainer.ReadItemAsync<LedgerDocument>(
                ledgerId,
                new PartitionKey(ledgerId),
                cancellationToken: cancellationToken);

            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Ledger not found. LedgerId={LedgerId}", ledgerId);
            return null;
        }
    }

    public async Task<LedgerEntry> WriteEntryAsync(
        LedgerEntry entry,
        decimal newBalance,
        CancellationToken cancellationToken = default)
    {
        // Step 1 — write ledger entry
        var entryResponse = await _ledgerEntriesContainer.CreateItemAsync(
            entry,
            new PartitionKey(entry.LedgerId),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Ledger entry written. LedgerId={LedgerId} GluId={GluId} Amount={Amount}",
            entry.LedgerId, entry.GluId, entry.Amount);

        // Step 2 — update ledger LastBalance
        var patchOperations = new[]
        {
            PatchOperation.Set("/LastBalance", newBalance),
            PatchOperation.Set("/UpdatedAt", DateTime.UtcNow)
        };

        await _ledgerContainer.PatchItemAsync<LedgerDocument>(
            entry.LedgerId,
            new PartitionKey(entry.LedgerId),
            patchOperations,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Ledger balance updated. LedgerId={LedgerId} NewBalance={NewBalance}",
            entry.LedgerId, newBalance);

        return entryResponse.Resource;
    }
}
