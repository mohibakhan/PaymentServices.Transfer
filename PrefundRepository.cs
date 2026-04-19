using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PaymentServices.Transfer.Models;

namespace PaymentServices.Transfer.Repositories;

public interface IPrefundRepository
{
    /// <summary>
    /// Resolves the RTPPrefund ledger ID for a given fintechId.
    /// Chain: fintechId → Platform → NonInd Customer → RTPPrefund Account → ledgerId
    /// </summary>
    Task<PrefundResolutionResult?> ResolvePrefundLedgerAsync(
        string fintechId,
        CancellationToken cancellationToken = default);
}

public sealed class PrefundRepository : IPrefundRepository
{
    private readonly Container _platformsContainer;
    private readonly Container _customersContainer;
    private readonly Container _accountsContainer;
    private readonly ILogger<PrefundRepository> _logger;

    public PrefundRepository(
        [FromKeyedServices("platforms")] Container platformsContainer,
        [FromKeyedServices("customers")] Container customersContainer,
        [FromKeyedServices("accounts")] Container accountsContainer,
        ILogger<PrefundRepository> logger)
    {
        _platformsContainer = platformsContainer;
        _customersContainer = customersContainer;
        _accountsContainer = accountsContainer;
        _logger = logger;
    }

    public async Task<PrefundResolutionResult?> ResolvePrefundLedgerAsync(
        string fintechId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Resolving RTPPrefund ledger. FintechId={FintechId}", fintechId);

        // Step 1 — find Platform by fintechId
        var platform = await GetPlatformByFintechIdAsync(fintechId, cancellationToken);
        if (platform is null)
        {
            _logger.LogWarning(
                "Platform not found. FintechId={FintechId}", fintechId);
            return null;
        }

        // Step 2 — get the NonIndividual customer (first customer on platform)
        var customerId = platform.CustomerIds.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(customerId))
        {
            _logger.LogWarning(
                "Platform has no customers. FintechId={FintechId} PlatformId={PlatformId}",
                fintechId, platform.Id);
            return null;
        }

        var customer = await GetCustomerAsync(customerId, cancellationToken);
        if (customer is null)
        {
            _logger.LogWarning(
                "NonInd customer not found. CustomerId={CustomerId}", customerId);
            return null;
        }

        // Step 3 — find RTPPrefund account from customer's accountIds
        var rtpPrefundAccount = await GetRtpPrefundAccountAsync(
            customer.AccountIds, cancellationToken);

        if (rtpPrefundAccount is null)
        {
            _logger.LogWarning(
                "RTPPrefund account not found. CustomerId={CustomerId}", customerId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(rtpPrefundAccount.LedgerId))
        {
            _logger.LogWarning(
                "RTPPrefund account has no ledgerId. AccountId={AccountId}",
                rtpPrefundAccount.Id);
            return null;
        }

        _logger.LogInformation(
            "RTPPrefund resolved. FintechId={FintechId} AccountId={AccountId} LedgerId={LedgerId} AccountNumber={AccountNumber}",
            fintechId, rtpPrefundAccount.Id, rtpPrefundAccount.LedgerId, rtpPrefundAccount.AccountNumber);

        return new PrefundResolutionResult
        {
            AccountId = rtpPrefundAccount.Id,
            AccountNumber = rtpPrefundAccount.AccountNumber,
            LedgerId = rtpPrefundAccount.LedgerId
        };
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<PlatformDocument?> GetPlatformByFintechIdAsync(
        string fintechId,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.fintechId = @fintechId")
            .WithParameter("@fintechId", fintechId);

        var results = new List<PlatformDocument>();
        using var iterator = _platformsContainer.GetItemQueryIterator<PlatformDocument>(
            query, requestOptions: new QueryRequestOptions { MaxItemCount = 1 });

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            results.AddRange(page);
        }

        return results.FirstOrDefault();
    }

    private async Task<CustomerDocument?> GetCustomerAsync(
        string customerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _customersContainer.ReadItemAsync<CustomerDocument>(
                customerId, new PartitionKey(customerId),
                cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<AccountDocument?> GetRtpPrefundAccountAsync(
        List<string> accountIds,
        CancellationToken cancellationToken)
    {
        foreach (var accountId in accountIds)
        {
            // Query by id — accounts are partitioned by accountNumber so cross-partition needed
            var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.id = @accountId AND c.kind = 'RTPPrefund'")
                .WithParameter("@accountId", accountId);

            var results = new List<AccountDocument>();
            using var iterator = _accountsContainer.GetItemQueryIterator<AccountDocument>(
                query, requestOptions: new QueryRequestOptions { MaxItemCount = 1 });

            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(cancellationToken);
                results.AddRange(page);
            }

            var account = results.FirstOrDefault();
            if (account is not null)
                return account;
        }

        return null;
    }
}
