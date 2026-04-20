using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PaymentServices.Transfer.Models;

namespace PaymentServices.Transfer.Repositories;

public interface ILedgerRepository
{
    Task<LedgerDocument?> GetLedgerAsync(
        string ledgerId,
        CancellationToken cancellationToken = default);

    Task<bool> EntryExistsAsync(
        string ledgerId,
        string evolveId,
        CancellationToken cancellationToken = default);

    Task<LedgerEntry?> GetEntryAsync(
        string ledgerId,
        string evolveId,
        CancellationToken cancellationToken = default);

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
                ledgerId, new PartitionKey(ledgerId),
                cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Ledger not found. LedgerId={LedgerId}", ledgerId);
            return null;
        }
    }

    public async Task<bool> EntryExistsAsync(
        string ledgerId,
        string evolveId,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition(
            "SELECT c.id FROM c WHERE c.LedgerId = @ledgerId AND c.TransactionId = @evolveId")
            .WithParameter("@ledgerId", ledgerId)
            .WithParameter("@evolveId", evolveId);

        using var iterator = _ledgerEntriesContainer.GetItemQueryIterator<dynamic>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(ledgerId),
                MaxItemCount = 1
            });

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            if (page.Any()) return true;
        }

        return false;
    }

    public async Task<LedgerEntry?> GetEntryAsync(
        string ledgerId,
        string evolveId,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.LedgerId = @ledgerId AND c.TransactionId = @evolveId")
            .WithParameter("@ledgerId", ledgerId)
            .WithParameter("@evolveId", evolveId);

        var results = new List<LedgerEntry>();
        using var iterator = _ledgerEntriesContainer.GetItemQueryIterator<LedgerEntry>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(ledgerId),
                MaxItemCount = 1
            });

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            results.AddRange(page);
        }

        return results.FirstOrDefault();
    }

    public async Task<LedgerEntry> WriteEntryAsync(
        LedgerEntry entry,
        decimal newBalance,
        CancellationToken cancellationToken = default)
    {
        // Write ledger entry
        var entryResponse = await _ledgerEntriesContainer.CreateItemAsync(
            entry, new PartitionKey(entry.LedgerId),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Ledger entry written. LedgerId={LedgerId} GluId={GluId} Amount={Amount}",
            entry.LedgerId, entry.GluId, entry.Amount);

        // Patch ledger LastBalance
        var patchOperations = new[]
        {
            PatchOperation.Set("/LastBalance", newBalance),
            PatchOperation.Set("/UpdatedAt", DateTime.UtcNow)
        };

        await _ledgerContainer.PatchItemAsync<LedgerDocument>(
            entry.LedgerId, new PartitionKey(entry.LedgerId),
            patchOperations,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Ledger balance updated. LedgerId={LedgerId} NewBalance={NewBalance}",
            entry.LedgerId, newBalance);

        return entryResponse.Resource;
    }
}
