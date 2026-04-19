using PaymentServices.Shared.Models;

namespace PaymentServices.Transfer.Models;

/// <summary>
/// Transfer-specific settings bound from <c>app:AppSettings</c>.
/// </summary>
public sealed class TransferSettings : AppSettings
{
    // -------------------------------------------------------------------------
    // Cosmos
    // -------------------------------------------------------------------------
    public string COSMOS_TRANSACTIONS_CONTAINER { get; set; } = "tchSendTransactions";

    /// <summary>Ledgers database name — separate from tptch.</summary>
    public string COSMOS_LEDGER_DATABASE { get; set; } = "ledgers";
    public string COSMOS_LEDGER_CONTAINER { get; set; } = "ledgers";
    public string COSMOS_LEDGER_ENTRIES_CONTAINER { get; set; } = "ledgerEntries";

    // -------------------------------------------------------------------------
    // Service Bus
    // -------------------------------------------------------------------------
    public string SERVICE_BUS_TRANSFER_SUBSCRIPTION { get; set; } = "transfer";

    // -------------------------------------------------------------------------
    // Feature flags — toggle debit/credit entries independently
    // -------------------------------------------------------------------------

    /// <summary>
    /// When true, writes a debit ledger entry on the source ledger.
    /// Set to false to disable source debit during testing.
    /// </summary>
    public bool WRITE_SOURCE_DEBIT { get; set; } = true;

    /// <summary>
    /// When true, writes a credit ledger entry on the destination ledger.
    /// Set to false to disable destination credit during testing.
    /// </summary>
    public bool WRITE_DESTINATION_CREDIT { get; set; } = true;
}
