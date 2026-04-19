using System.Text.Json.Serialization;

namespace PaymentServices.Transfer.Models;

/// <summary>
/// Ledger document — maps to `ledgers` container.
/// Partition key: /id
/// </summary>
public sealed class LedgerDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("AccountNumber")]
    public required string AccountNumber { get; set; }

    [JsonPropertyName("Currency")]
    public LedgerCurrency Currency { get; set; } = new();

    [JsonPropertyName("LastBalance")]
    public decimal LastBalance { get; set; }

    [JsonPropertyName("Metadata")]
    public LedgerDocumentMetadata Metadata { get; set; } = new();

    [JsonPropertyName("LedgerType")]
    public string LedgerType { get; set; } = "prefund-ledger-v2";

    [JsonPropertyName("CreatedAt")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("UpdatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class LedgerCurrency
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "USD";

    [JsonPropertyName("Symbol")]
    public string Symbol { get; set; } = "USD";

    [JsonPropertyName("BaseUnit")]
    public string BaseUnit { get; set; } = "Cent";

    [JsonPropertyName("Decimals")]
    public int Decimals { get; set; } = 2;
}

public sealed class LedgerDocumentMetadata
{
    [JsonPropertyName("accountId")]
    public string? AccountId { get; set; }
}

/// <summary>
/// Ledger entry document — maps to `ledgerEntries` container.
/// Partition key: /LedgerId
/// Matches existing schema exactly.
/// </summary>
public sealed class LedgerEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    [JsonPropertyName("LedgerId")]
    public required string LedgerId { get; init; }

    [JsonPropertyName("AccountNumber")]
    public required string AccountNumber { get; init; }

    /// <summary>
    /// Positive for credit, negative for debit.
    /// Stored as decimal matching existing Amount field.
    /// </summary>
    [JsonPropertyName("Amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("GluId")]
    public string GluId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Maps to evolveId for tracing.</summary>
    [JsonPropertyName("TransactionId")]
    public required string TransactionId { get; init; }

    [JsonPropertyName("Kind")]
    public string Kind { get; init; } = "tptch.send";

    [JsonPropertyName("Status")]
    public string Status { get; init; } = "Completed";

    [JsonPropertyName("Metadata")]
    public LedgerEntryMetadata Metadata { get; init; } = new();

    [JsonPropertyName("CreatedAt")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class LedgerEntryMetadata
{
    [JsonPropertyName("postedAt")]
    public DateTime PostedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("evolveId")]
    public string? EvolveId { get; init; }
}

/// <summary>
/// Result of writing ledger entries for a payment.
/// </summary>
public sealed class TransferResult
{
    /// <summary>GluId of the source debit entry. Null if WRITE_SOURCE_DEBIT is false.</summary>
    public string? GluIdSource { get; init; }

    /// <summary>GluId of the destination credit entry. Null if WRITE_DESTINATION_CREDIT is false.</summary>
    public string? GluIdDestination { get; init; }

    /// <summary>Internal transaction ID — evolveId used for tracing.</summary>
    public required string EveTransactionId { get; init; }
}
