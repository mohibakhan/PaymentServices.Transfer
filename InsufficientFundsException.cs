namespace PaymentServices.Transfer.Exceptions;

/// <summary>
/// Thrown when the source ledger doesn't have enough funds to cover the
/// transfer. Terminal — the transfer fails and the caller is notified;
/// retrying won't change the balance.
/// </summary>
public sealed class InsufficientFundsException : Exception
{
    public decimal CurrentBalance { get; }
    public decimal RequestedAmount { get; }
    public decimal ProjectedBalance { get; }

    public InsufficientFundsException(
        decimal currentBalance,
        decimal requestedAmount,
        decimal projectedBalance,
        string message)
        : base(message)
    {
        CurrentBalance = currentBalance;
        RequestedAmount = requestedAmount;
        ProjectedBalance = projectedBalance;
    }
}
