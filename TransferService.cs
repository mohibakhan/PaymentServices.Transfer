using Microsoft.Extensions.Logging;
using PaymentServices.Shared.Messages;
using PaymentServices.Transfer.Exceptions;
using PaymentServices.Transfer.Models;

namespace PaymentServices.Transfer.Services;

public interface ITransferService
{
    Task<TransferResult> ExecuteAsync(
        PaymentMessage message,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs the checks for a transfer:
///   1. LIMIT check (mock)
///   2. SCREENING check (mock)
///   3. LEDGER source debit via the Evolve NuGet (NSF terminal)
///
/// Ledger logic mirrors the former RTPSend EvolveLedgerService — resolve the
/// source ledger by account number, NSF-check, post one negative debit entry.
/// Destination credit is intentionally NOT performed (source debit only).
/// </summary>
public sealed class TransferService : ITransferService
{
    private readonly ILimitService _limitService;
    private readonly IScreeningService _screeningService;
    private readonly ILedgerService _ledgerService;
    private readonly ILogger<TransferService> _logger;

    public TransferService(
        ILimitService limitService,
        IScreeningService screeningService,
        ILedgerService ledgerService,
        ILogger<TransferService> logger)
    {
        _limitService = limitService;
        _screeningService = screeningService;
        _ledgerService = ledgerService;
        _logger = logger;
    }

    public async Task<TransferResult> ExecuteAsync(
        PaymentMessage message,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Transfer executing. EvolveId={EvolveId} Amount={Amount} FintechId={FintechId}",
            message.EvolveId, message.Amount, message.FintechId);

        // ---- LIMIT --------------------------------------------------------
        var limit = await _limitService.CheckAsync(message, cancellationToken);
        if (!limit.Allowed)
        {
            throw new LimitExceededException(limit.Reason ?? "Limit check denied");
        }

        // ---- SCREENING ----------------------------------------------------
        var screening = await _screeningService.CheckAsync(message, cancellationToken);
        if (!screening.Allowed)
        {
            throw new ScreeningRejectedException(screening.Reason ?? "Screening rejected");
        }

        // ---- LEDGER (source debit) ---------------------------------------
        // NSF throws InsufficientFundsException (terminal). Other failures
        // return a Failed result which we turn into a retryable exception.
        var ledgerResult = await _ledgerService.ReserveAsync(new LedgerReservationRequest
        {
            EvolveId = message.EvolveId,
            FintechId = message.FintechId,
            CorrelationId = message.CorrelationId,
            SourceAccountNumber = message.Source.AccountNumber,
            Amount = message.Amount
        }, cancellationToken);

        if (!ledgerResult.Success)
        {
            throw new InvalidOperationException(
                ledgerResult.Reason ?? "Ledger reservation failed");
        }

        _logger.LogInformation(
            "Transfer ledger debit complete. EvolveId={EvolveId} LedgerEntryId={LedgerEntryId}",
            message.EvolveId, ledgerResult.ReservationId);

        return new TransferResult
        {
            GluIdSource = ledgerResult.ReservationId,
            GluIdDestination = null,           // source debit only
            EveTransactionId = message.EvolveId
        };
    }
}

/// <summary>Terminal — limit check denied the transfer.</summary>
public sealed class LimitExceededException : Exception
{
    public LimitExceededException(string message) : base(message) { }
}

/// <summary>Terminal — screening/compliance rejected the transfer.</summary>
public sealed class ScreeningRejectedException : Exception
{
    public ScreeningRejectedException(string message) : base(message) { }
}
