namespace LedgerPay.Domain;

/// <summary>One side of a double-entry posting. Positive increases the account, negative decreases it.</summary>
public record LedgerLine(string Account, long Amount);

/// <summary>
/// Turns money-moving events into balanced postings. Every posting sums to zero, so the
/// whole ledger always sums to zero ("trial balance"). Ledger accounts:
///   wallet:{id}          customer money we hold (what the wallet balance shows)
///   external:{currency}  money that entered or left the platform (card top-ups, bank withdrawals)
///   clearing:{currency}  money in flight between the two legs of a transfer; nets to zero
/// </summary>
public static class Ledger
{
    public static string Wallet(Guid id) => $"wallet:{id}";
    public static string External(string currency) => $"external:{currency}";
    public static string Clearing(string currency) => $"clearing:{currency}";

    public static IReadOnlyList<LedgerLine> Post(IEvent e, string currency) => e switch
    {
        FundsDeposited d => [new(Wallet(d.AccountId), d.Amount), new(External(currency), -d.Amount)],
        FundsWithdrawn w => [new(Wallet(w.AccountId), -w.Amount), new(External(currency), w.Amount)],
        TransferSent s => [new(Wallet(s.AccountId), -s.Amount), new(Clearing(currency), s.Amount)],
        TransferReceived r => [new(Clearing(currency), -r.Amount), new(Wallet(r.AccountId), r.Amount)],
        _ => [],
    };
}

public static class Amounts
{
    /// <summary>Converts a decimal amount with at most two decimal places into minor units.</summary>
    public static long ToMinor(decimal amount)
    {
        if (decimal.Round(amount, 2) != amount) throw new DomainException("Amounts can have at most two decimal places.");
        return (long)(amount * 100);
    }

    public static decimal ToMajor(long minor) => minor / 100m;
}
