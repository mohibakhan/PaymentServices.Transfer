using System.Text.Json.Serialization;

namespace PaymentServices.Transfer.Models;

// ---------------------------------------------------------------------------
// Ledger models — ledgers database
// ---------------------------------------------------------------------------

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

public sealed class LedgerEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    [JsonPropertyName("LedgerId")]
    public required string LedgerId { get; init; }

    [JsonPropertyName("AccountNumber")]
    public required string AccountNumber { get; init; }

    /// <summary>Negative for debit, positive for credit.</summary>
    [JsonPropertyName("Amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("GluId")]
    public string GluId { get; init; } = Guid.NewGuid().ToString();

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

// ---------------------------------------------------------------------------
// tptch container models — used for RTPPrefund resolution
// ---------------------------------------------------------------------------

public sealed class PlatformDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("fintechId")]
    public string? FintechId { get; init; }

    [JsonPropertyName("customerIds")]
    public List<string> CustomerIds { get; init; } = [];
}

public sealed class CustomerDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("accountIds")]
    public List<string> AccountIds { get; init; } = [];
}

public sealed class AccountDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("accountNumber")]
    public string AccountNumber { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("ledgerId")]
    public string? LedgerId { get; init; }
}

// ---------------------------------------------------------------------------
// Resolution results
// ---------------------------------------------------------------------------

/// <summary>
/// Result of resolving the RTPPrefund account for a given fintechId.
/// </summary>
public sealed class PrefundResolutionResult
{
    public required string AccountId { get; init; }
    public required string AccountNumber { get; init; }
    public required string LedgerId { get; init; }
}

/// <summary>
/// Result of executing a transfer.
/// </summary>
public sealed class TransferResult
{
    /// <summary>GluId of the RTPPrefund debit entry.</summary>
    public string? GluIdSource { get; init; }

    /// <summary>GluId of the destination credit entry.</summary>
    public string? GluIdDestination { get; init; }

    public required string EveTransactionId { get; init; }
}
