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
    private readonly TransferSettings _settings;
    private readonly ILogger<TransferService> _logger;

    public TransferService(
        ILedgerRepository ledgerRepository,
        IOptions<TransferSettings> settings,
        ILogger<TransferService> logger)
    {
        _ledgerRepository = ledgerRepository;
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
            "Transfer executing. EvolveId={EvolveId} Amount={Amount} WriteSourceDebit={WriteSourceDebit} WriteDestinationCredit={WriteDestinationCredit}",
            message.EvolveId, amount, _settings.WRITE_SOURCE_DEBIT, _settings.WRITE_DESTINATION_CREDIT);

        // -------------------------------------------------------------------------
        // Debit source ledger
        // -------------------------------------------------------------------------
        if (_settings.WRITE_SOURCE_DEBIT)
        {
            // Idempotency check — was this entry already written on a previous attempt?
            var sourceEntryExists = await _ledgerRepository.EntryExistsAsync(
                message.Source.LedgerId!, message.EvolveId, cancellationToken);

            if (sourceEntryExists)
            {
                _logger.LogWarning(
                    "Source ledger entry already exists, skipping write. EvolveId={EvolveId} LedgerId={LedgerId}",
                    message.EvolveId, message.Source.LedgerId);

                // Retrieve existing entry to get its GluId for consistent response
                var existingEntry = await _ledgerRepository.GetEntryAsync(
                    message.Source.LedgerId!, message.EvolveId, cancellationToken);
                gluIdSource = existingEntry?.GluId;
            }
            else
            {
                var sourceLedger = await _ledgerRepository.GetLedgerAsync(
                    message.Source.LedgerId!, cancellationToken);

                if (sourceLedger is null)
                    throw new InvalidOperationException(
                        $"Source ledger not found. LedgerId={message.Source.LedgerId}");

                var sourceEntry = new LedgerEntry
                {
                    LedgerId = message.Source.LedgerId!,
                    AccountNumber = message.Source.AccountNumber,
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

                var newSourceBalance = sourceLedger.LastBalance - amount;
                var writtenSourceEntry = await _ledgerRepository.WriteEntryAsync(
                    sourceEntry, newSourceBalance, cancellationToken);

                gluIdSource = writtenSourceEntry.GluId;

                _logger.LogInformation(
                    "Source debit written. EvolveId={EvolveId} LedgerId={LedgerId} GluId={GluId} NewBalance={NewBalance}",
                    message.EvolveId, message.Source.LedgerId, gluIdSource, newSourceBalance);
            }
        }

        // -------------------------------------------------------------------------
        // Credit destination ledger
        // -------------------------------------------------------------------------
        if (_settings.WRITE_DESTINATION_CREDIT)
        {
            // Idempotency check — was this entry already written on a previous attempt?
            var destinationEntryExists = await _ledgerRepository.EntryExistsAsync(
                message.Destination.LedgerId!, message.EvolveId, cancellationToken);

            if (destinationEntryExists)
            {
                _logger.LogWarning(
                    "Destination ledger entry already exists, skipping write. EvolveId={EvolveId} LedgerId={LedgerId}",
                    message.EvolveId, message.Destination.LedgerId);

                // Retrieve existing entry to get its GluId for consistent response
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

                var destinationEntry = new LedgerEntry
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
                var writtenDestinationEntry = await _ledgerRepository.WriteEntryAsync(
                    destinationEntry, newDestinationBalance, cancellationToken);

                gluIdDestination = writtenDestinationEntry.GluId;

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
