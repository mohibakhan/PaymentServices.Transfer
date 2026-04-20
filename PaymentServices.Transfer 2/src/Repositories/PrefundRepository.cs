using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaymentServices.Transfer.Models;
using System.Collections.Concurrent;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;

namespace PaymentServices.Transfer.Repositories;

public interface IPrefundRepository
{
    /// <summary>
    /// Resolves the RTPPrefund ledger ID for a given fintechId.
    /// Results are cached in-memory for the lifetime of the function app instance.
    /// Chain: fintechId → Platform → NonInd Customer → RTPPrefund Account → ledgerId
    /// </summary>
    Task<PrefundResolutionResult?> ResolvePrefundLedgerAsync(
        string fintechId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the in-memory cache for a given fintechId.
    /// Call if a platform's RTPPrefund account changes.
    /// </summary>
    void InvalidateCache(string fintechId);
}

public sealed class PrefundRepository : IPrefundRepository
{
    private readonly CosmosContainer _platformsContainer;
    private readonly CosmosContainer _customersContainer;
    private readonly CosmosContainer _accountsContainer;
    private readonly ILogger<PrefundRepository> _logger;

    /// <summary>
    /// In-memory cache — keyed by fintechId.
    /// RTPPrefund data almost never changes so this is safe to cache
    /// for the lifetime of the function app instance.
    /// On restart (deploy, scale-out) the cache is rebuilt on first payment.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PrefundResolutionResult>
        _cache = new(StringComparer.OrdinalIgnoreCase);

    public PrefundRepository(
        [FromKeyedServices("platforms")] CosmosContainer platformsContainer,
        [FromKeyedServices("customers")] CosmosContainer customersContainer,
        [FromKeyedServices("accounts")] CosmosContainer accountsContainer,
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
        // -------------------------------------------------------------------------
        // Check in-memory cache first — saves 3 Cosmos reads on every payment
        // after the first resolution per fintechId per instance
        // -------------------------------------------------------------------------
        if (_cache.TryGetValue(fintechId, out var cached))
        {
            _logger.LogDebug(
                "RTPPrefund resolved from cache. FintechId={FintechId} LedgerId={LedgerId}",
                fintechId, cached.LedgerId);
            return cached;
        }

        _logger.LogInformation(
            "Cache miss — resolving RTPPrefund from Cosmos. FintechId={FintechId}", fintechId);

        // -------------------------------------------------------------------------
        // Resolve from Cosmos
        // -------------------------------------------------------------------------
        var result = await ResolveFromCosmosAsync(fintechId, cancellationToken);

        if (result is null)
            return null;

        // -------------------------------------------------------------------------
        // Store in cache for all subsequent payments on this instance
        // -------------------------------------------------------------------------
        _cache[fintechId] = result;

        _logger.LogInformation(
            "RTPPrefund cached. FintechId={FintechId} AccountId={AccountId} LedgerId={LedgerId}",
            fintechId, result.AccountId, result.LedgerId);

        return result;
    }

    public void InvalidateCache(string fintechId)
    {
        if (_cache.TryRemove(fintechId, out _))
            _logger.LogInformation(
                "RTPPrefund cache invalidated. FintechId={FintechId}", fintechId);
    }

    // -------------------------------------------------------------------------
    // Private — Cosmos resolution chain
    // -------------------------------------------------------------------------

    private async Task<PrefundResolutionResult?> ResolveFromCosmosAsync(
        string fintechId,
        CancellationToken cancellationToken)
    {
        // Step 1 — find Platform by fintechId
        var platform = await GetPlatformByFintechIdAsync(fintechId, cancellationToken);
        if (platform is null)
        {
            _logger.LogWarning("Platform not found. FintechId={FintechId}", fintechId);
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
            "RTPPrefund resolved from Cosmos. FintechId={FintechId} AccountId={AccountId} LedgerId={LedgerId} AccountNumber={AccountNumber}",
            fintechId, rtpPrefundAccount.Id, rtpPrefundAccount.LedgerId, rtpPrefundAccount.AccountNumber);

        return new PrefundResolutionResult
        {
            AccountId = rtpPrefundAccount.Id,
            AccountNumber = rtpPrefundAccount.AccountNumber,
            LedgerId = rtpPrefundAccount.LedgerId
        };
    }

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
