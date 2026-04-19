using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentServices.Shared.Messages;
using PaymentServices.Transfer.Models;
using PaymentServices.Transfer.Repositories;

namespace PaymentServices.Transfer.Services;

public interface ITransferService
{
    Task<TransferResult> ExecuteAsync(
        PaymentMessage message,
        CancellationToken cancellationToken = default);
}

public sealed class TransferService : ITransferService
{
    private readonly ILedgerRepository _ledgerRepository;
    private readonly IPrefundRepository _prefundRepository;
    private readonly TransferSettings _settings;
    private readonly ILogger<TransferService> _logger;

    public TransferService(
        ILedgerRepository ledgerRepository,
        IPrefundRepository prefundRepository,
        IOptions<TransferSettings> settings,
        ILogger<TransferService> logger)
    {
        _ledgerRepository = ledgerRepository;
        _prefundRepository = prefundRepository;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<TransferResult> ExecuteAsync(
        PaymentMessage message,
        CancellationToken cancellationToken = default)
    {
        var amount = decimal.Parse(message.Amount);
        string? gluIdSource = null;
        string? gluIdDestination = null;

        _logger.LogInformation(
            "Transfer executing. EvolveId={EvolveId} Amount={Amount} FintechId={FintechId}",
            message.EvolveId, amount, message.FintechId);

        // -------------------------------------------------------------------------
        // Resolve RTPPrefund ledger from fintechId
        // -------------------------------------------------------------------------
        var prefund = await _prefundRepository.ResolvePrefundLedgerAsync(
            message.FintechId, cancellationToken);

        if (prefund is null)
            throw new InvalidOperationException(
                $"RTPPrefund account not found for FintechId={message.FintechId}");

        _logger.LogInformation(
            "RTPPrefund resolved. EvolveId={EvolveId} PrefundLedgerId={PrefundLedgerId} AccountNumber={AccountNumber}",
            message.EvolveId, prefund.LedgerId, prefund.AccountNumber);

        // -------------------------------------------------------------------------
        // Debit RTPPrefund ledger
        // -------------------------------------------------------------------------
        if (_settings.WRITE_SOURCE_DEBIT)
        {
            // Idempotency check
            var sourceEntryExists = await _ledgerRepository.EntryExistsAsync(
                prefund.LedgerId, message.EvolveId, cancellationToken);

            if (sourceEntryExists)
            {
                _logger.LogWarning(
                    "RTPPrefund debit entry already exists, skipping. EvolveId={EvolveId} LedgerId={LedgerId}",
                    message.EvolveId, prefund.LedgerId);

                var existingEntry = await _ledgerRepository.GetEntryAsync(
                    prefund.LedgerId, message.EvolveId, cancellationToken);
                gluIdSource = existingEntry?.GluId;
            }
            else
            {
                var prefundLedger = await _ledgerRepository.GetLedgerAsync(
                    prefund.LedgerId, cancellationToken);

                if (prefundLedger is null)
                    throw new InvalidOperationException(
                        $"RTPPrefund ledger document not found. LedgerId={prefund.LedgerId}");

                var debitEntry = new LedgerEntry
                {
                    LedgerId = prefund.LedgerId,
                    AccountNumber = prefund.AccountNumber,
                    Amount = -amount, // negative = debit
                    TransactionId = message.EvolveId,
                    Kind = "tptch.send",
                    Status = "Completed",
                    Metadata = new LedgerEntryMetadata
                    {
                        PostedAt = DateTime.UtcNow,
                        CorrelationId = message.CorrelationId,
                        EvolveId = message.EvolveId
                    }
                };

                var newPrefundBalance = prefundLedger.LastBalance - amount;
                var writtenDebitEntry = await _ledgerRepository.WriteEntryAsync(
                    debitEntry, newPrefundBalance, cancellationToken);

                gluIdSource = writtenDebitEntry.GluId;

                _logger.LogInformation(
                    "RTPPrefund debit written. EvolveId={EvolveId} LedgerId={LedgerId} GluId={GluId} NewBalance={NewBalance}",
                    message.EvolveId, prefund.LedgerId, gluIdSource, newPrefundBalance);
            }
        }

        // -------------------------------------------------------------------------
        // Credit destination ledger
        // -------------------------------------------------------------------------
        if (_settings.WRITE_DESTINATION_CREDIT)
        {
            // Idempotency check
            var destinationEntryExists = await _ledgerRepository.EntryExistsAsync(
                message.Destination.LedgerId!, message.EvolveId, cancellationToken);

            if (destinationEntryExists)
            {
                _logger.LogWarning(
                    "Destination credit entry already exists, skipping. EvolveId={EvolveId} LedgerId={LedgerId}",
                    message.EvolveId, message.Destination.LedgerId);

                var existingEntry = await _ledgerRepository.GetEntryAsync(
                    message.Destination.LedgerId!, message.EvolveId, cancellationToken);
                gluIdDestination = existingEntry?.GluId;
            }
            else
            {
                var destinationLedger = await _ledgerRepository.GetLedgerAsync(
                    message.Destination.LedgerId!, cancellationToken);

                if (destinationLedger is null)
                    throw new InvalidOperationException(
                        $"Destination ledger not found. LedgerId={message.Destination.LedgerId}");

                var creditEntry = new LedgerEntry
                {
                    LedgerId = message.Destination.LedgerId!,
                    AccountNumber = message.Destination.AccountNumber,
                    Amount = amount, // positive = credit
                    TransactionId = message.EvolveId,
                    Kind = "tptch.send",
                    Status = "Completed",
                    Metadata = new LedgerEntryMetadata
                    {
                        PostedAt = DateTime.UtcNow,
                        CorrelationId = message.CorrelationId,
                        EvolveId = message.EvolveId
                    }
                };

                var newDestinationBalance = destinationLedger.LastBalance + amount;
                var writtenCreditEntry = await _ledgerRepository.WriteEntryAsync(
                    creditEntry, newDestinationBalance, cancellationToken);

                gluIdDestination = writtenCreditEntry.GluId;

                _logger.LogInformation(
                    "Destination credit written. EvolveId={EvolveId} LedgerId={LedgerId} GluId={GluId} NewBalance={NewBalance}",
                    message.EvolveId, message.Destination.LedgerId, gluIdDestination, newDestinationBalance);
            }
        }

        return new TransferResult
        {
            GluIdSource = gluIdSource,
            GluIdDestination = gluIdDestination,
            EveTransactionId = message.EvolveId
        };
    }
}
