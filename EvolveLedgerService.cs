using Evolve.Digital.LedgerService.Shared.Internal;
using Evolve.Digital.LedgerService.Shared.Internal.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PaymentServices.Transfer.Exceptions;

namespace PaymentServices.Transfer.Services;

/// <summary>Request to reserve (debit) funds on the source ledger.</summary>
public sealed class LedgerReservationRequest
{
    public string EvolveId { get; init; } = string.Empty;
    public string FintechId { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string SourceAccountNumber { get; init; } = string.Empty;
    public string Amount { get; init; } = string.Empty;
}

public sealed class LedgerReservationResult
{
    public bool Success { get; init; }
    public string? ReservationId { get; init; }
    public string? Reason { get; init; }
    public static LedgerReservationResult Ok(string reservationId) =>
        new() { Success = true, ReservationId = reservationId };
    public static LedgerReservationResult Failed(string reason) =>
        new() { Success = false, Reason = reason };
}

/// <summary>
/// Ledger operations backed by the Evolve.Digital.LedgerService NuGet.
/// Mirrors the logic previously in RTPSend's EvolveLedgerService: resolve the
/// source ledger by account number, NSF-check, then post a single negative
/// (debit) entry. Source debit only — no destination credit.
/// </summary>
public interface ILedgerService
{
    Task<LedgerReservationResult> ReserveAsync(LedgerReservationRequest request, CancellationToken cancellationToken = default);
}

public sealed class EvolveLedgerService : ILedgerService
{
    private const string LedgerEntryKind = "rtp.send";

    private readonly ILedgerInternalClient _ledgerClient;
    private readonly ILogger<EvolveLedgerService> _logger;

    public EvolveLedgerService(
        ILedgerInternalClient ledgerClient,
        ILogger<EvolveLedgerService> logger)
    {
        _ledgerClient = ledgerClient;
        _logger = logger;
    }

    public async Task<LedgerReservationResult> ReserveAsync(
        LedgerReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!decimal.TryParse(request.Amount, out var amountDecimal))
        {
            _logger.LogError("Invalid amount '{Amount}' for evolveId {EvolveId}",
                request.Amount, request.EvolveId);
            return LedgerReservationResult.Failed($"Amount '{request.Amount}' is not a valid decimal");
        }

        var ledger = await _ledgerClient.GetLedgerByAccountAsync(request.SourceAccountNumber);
        if (ledger is null)
        {
            _logger.LogError(
                "Ledger not found for source account {AccountNumber} (evolveId {EvolveId})",
                request.SourceAccountNumber, request.EvolveId);
            return LedgerReservationResult.Failed($"Ledger not found for account {request.SourceAccountNumber}");
        }

        var nsf = await _ledgerClient.CheckNsfAsync(ledger.id, amountDecimal);
        if (nsf.ProjectedBalance < 0)
        {
            _logger.LogWarning(
                "Insufficient funds on ledger {LedgerId} (evolveId {EvolveId}): balance={Balance}, requested={Amount}, projected={Projected}",
                ledger.id, request.EvolveId, nsf.Balance, amountDecimal, nsf.ProjectedBalance);

            throw new InsufficientFundsException(
                currentBalance: nsf.Balance,
                requestedAmount: amountDecimal,
                projectedBalance: nsf.ProjectedBalance,
                message: $"Insufficient funds on account {request.SourceAccountNumber}: " +
                         $"balance {nsf.Balance:F2}, requested {amountDecimal:F2}");
        }

        var metadata = new Dictionary<string, object>
        {
            { "gluId", Guid.NewGuid().ToString() },
            { "Account", request.SourceAccountNumber },
            { "evolveId", request.EvolveId },
            { "correlationId", request.CorrelationId },
            { "fintechId", request.FintechId },
            { "endpoint", "tptch.send" }
        };

        var addEntryRequest = new AddEntryRequest(
            LedgerId: ledger.id,
            Amount: -amountDecimal,           // debit — negative
            Trace: new { evolveId = request.EvolveId },
            Kind: LedgerEntryKind,
            Metadata: metadata,
            IsRemoteAccount: false);

        try
        {
            var entryId = await _ledgerClient.AddEntryAsync(addEntryRequest);
            _logger.LogInformation(
                "Ledger entry {EntryId} posted on ledger {LedgerId} for evolveId {EvolveId} amount {Amount}",
                entryId, ledger.id, request.EvolveId, -amountDecimal);
            return LedgerReservationResult.Ok(entryId);
        }
        catch (CosmosException cex)
        {
            _logger.LogError(
                "CosmosException posting ledger debit: StatusCode={Status} SubStatus={SubStatus} ActivityId={Activity} Message={Message}",
                cex.StatusCode, cex.SubStatusCode, cex.ActivityId, cex.Message);
            return LedgerReservationResult.Failed($"Ledger write failed: {cex.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to post ledger entry for evolveId {EvolveId} on ledger {LedgerId}",
                request.EvolveId, ledger.id);
            return LedgerReservationResult.Failed($"AddEntry failed on ledger {ledger.id}: {ex.Message}");
        }
    }
}
