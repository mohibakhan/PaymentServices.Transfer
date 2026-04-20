using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PaymentServices.Shared.Enums;
using PaymentServices.Shared.Infrastructure;
using PaymentServices.Shared.Interfaces;
using PaymentServices.Shared.Messages;
using PaymentServices.Transfer.Repositories;
using PaymentServices.Transfer.Services;

namespace PaymentServices.Transfer.Functions;

/// <summary>
/// Service Bus Trigger — subscribed to transfer subscription (state: TmsCompleted).
/// Writes debit/credit ledger entries and updates ledger balances.
/// On success → publishes TransferCompleted → EventNotification.
/// On failure → publishes TransferFailed → EventNotification.
/// </summary>
public sealed class TransferFunction
{
    private readonly ITransferService _transferService;
    private readonly ITransactionStateRepository _transactionStateRepository;
    private readonly IServiceBusPublisher _publisher;
    private readonly ILogger<TransferFunction> _logger;

    public TransferFunction(
        ITransferService transferService,
        ITransactionStateRepository transactionStateRepository,
        IServiceBusPublisher publisher,
        ILogger<TransferFunction> logger)
    {
        _transferService = transferService;
        _transactionStateRepository = transactionStateRepository;
        _publisher = publisher;
        _logger = logger;
    }

    [Function(nameof(TransferFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger(
            topicName: "%app:AppSettings:SERVICE_BUS_TOPIC%",
            subscriptionName: "%app:AppSettings:SERVICE_BUS_TRANSFER_SUBSCRIPTION%",
            Connection = "app:AppSettings:SERVICE_BUS_CONNSTRING")]
        ServiceBusReceivedMessage serviceBusMessage,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        PaymentMessage? message = null;

        try
        {
            message = ServiceBusPublisher.Deserialize(serviceBusMessage);

            _logger.LogInformation(
                "Transfer started. EvolveId={EvolveId} CorrelationId={CorrelationId} Amount={Amount}",
                message.EvolveId, message.CorrelationId, message.Amount);

            // Update state to TransferPending
            await _transactionStateRepository.UpdateStateAsync(
                message.EvolveId,
                TransactionState.TransferPending,
                cancellationToken: cancellationToken);

            // Execute ledger writes
            var result = await _transferService.ExecuteAsync(message, cancellationToken);

            // Enrich message with transfer result
            message.EveTransactionId = result.EveTransactionId;
            message.GluIdSource = result.GluIdSource;
            message.GluIdDestination = result.GluIdDestination;
            message.State = TransactionState.TransferCompleted;

            // Update Cosmos transaction state
            await _transactionStateRepository.UpdateStateAsync(
                message.EvolveId,
                TransactionState.TransferCompleted,
                tx =>
                {
                    tx.EveTransactionId = result.EveTransactionId;
                    tx.GluIdSource = result.GluIdSource;
                    tx.GluIdDestination = result.GluIdDestination;
                },
                cancellationToken);

            // Publish to EventNotification
            await _publisher.PublishAsync(message, cancellationToken);

            _logger.LogInformation(
                "Transfer completed. EvolveId={EvolveId} GluIdSource={GluIdSource} GluIdDestination={GluIdDestination}",
                message.EvolveId, result.GluIdSource, result.GluIdDestination);

            await messageActions.CompleteMessageAsync(serviceBusMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Transfer exception. EvolveId={EvolveId} CorrelationId={CorrelationId}",
                message?.EvolveId ?? "unknown", message?.CorrelationId ?? "unknown");

            // Try to update state to TransferFailed and notify
            if (message is not null)
            {
                try
                {
                    message.State = TransactionState.TransferFailed;
                    message.FailureReason = ex.Message;

                    await _transactionStateRepository.UpdateStateAsync(
                        message.EvolveId,
                        TransactionState.TransferFailed,
                        tx => tx.FailureReason = ex.Message,
                        cancellationToken);

                    await _publisher.PublishAsync(message, cancellationToken);
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx,
                        "Failed to publish TransferFailed. EvolveId={EvolveId}",
                        message.EvolveId);
                }
            }

            await messageActions.DeadLetterMessageAsync(
                serviceBusMessage,
                deadLetterReason: "UnhandledException",
                deadLetterErrorDescription: ex.Message,
                cancellationToken: cancellationToken);
        }
    }
}
