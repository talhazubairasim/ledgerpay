namespace LedgerPay.Domain;

/// <summary>
/// A wallet, rebuilt by replaying its events. Commands validate against the replayed state
/// and return the new events; nothing is mutated until those events are stored.
/// </summary>
public class Account
{
    public static readonly string[] Currencies = ["PKR", "USD", "EUR"];
    /// <summary>Maximum outgoing (withdrawals + transfers) in any rolling 24 hours: 500,000.00.</summary>
    public const long DailyOutgoingLimit = 50_000_000;
    public const long MaxDeposit = 100_000_000;

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Number { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string Currency { get; private set; } = "";
    public long Balance { get; private set; }
    public bool Frozen { get; private set; }
    public int Version { get; private set; } = -1;

    /// <summary>Outgoing transfers, used by the daily limit and the fraud rules.</summary>
    public List<(Guid To, long Amount, DateTimeOffset At)> Sent { get; } = new();
    private readonly List<(long Amount, DateTimeOffset At)> _outgoing = new();

    public static Account Replay(IEnumerable<IEvent> events)
    {
        var account = new Account();
        foreach (var e in events) account.Apply(e);
        return account;
    }

    public void Apply(IEvent e)
    {
        switch (e)
        {
            case AccountOpened o: Id = o.AccountId; OwnerId = o.OwnerId; Number = o.Number; Name = o.Name; Currency = o.Currency; break;
            case FundsDeposited d: Balance += d.Amount; break;
            case FundsWithdrawn w: Balance -= w.Amount; _outgoing.Add((w.Amount, w.At)); break;
            case TransferSent s: Balance -= s.Amount; _outgoing.Add((s.Amount, s.At)); Sent.Add((s.ToAccountId, s.Amount, s.At)); break;
            case TransferReceived r: Balance += r.Amount; break;
            case AccountFrozen: Frozen = true; break;
            case AccountUnfrozen: Frozen = false; break;
        }
        Version++;
    }

    public static AccountOpened Open(Guid ownerId, string name, string currency, string number, DateTimeOffset now)
    {
        if (!Currencies.Contains(currency)) throw new DomainException($"Currency must be one of {string.Join(", ", Currencies)}.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Give the wallet a name.");
        return new AccountOpened(Guid.NewGuid(), ownerId, number, name.Trim(), currency, now);
    }

    public FundsDeposited Deposit(long amount, string reference, DateTimeOffset now)
    {
        EnsureOpen();
        if (amount <= 0) throw new DomainException("Amount must be positive.");
        if (amount > MaxDeposit) throw new DomainException("A single deposit can be at most 1,000,000.00.");
        return new FundsDeposited(Id, amount, reference, now);
    }

    public FundsWithdrawn Withdraw(long amount, string reference, DateTimeOffset now)
    {
        CheckOutgoing(amount, now);
        return new FundsWithdrawn(Id, amount, reference, now);
    }

    public TransferSent Send(Guid transferId, Guid to, long amount, DateTimeOffset now)
    {
        CheckOutgoing(amount, now);
        return new TransferSent(Id, transferId, to, amount, now);
    }

    public TransferReceived Receive(Guid transferId, Guid from, long amount, DateTimeOffset now)
    {
        EnsureOpen();
        if (amount <= 0) throw new DomainException("Amount must be positive.");
        return new TransferReceived(Id, transferId, from, amount, now);
    }

    public AccountFrozen Freeze(string reason, DateTimeOffset now)
    {
        if (Frozen) throw new DomainException("The account is already frozen.");
        return new AccountFrozen(Id, reason, now);
    }

    public AccountUnfrozen Unfreeze(DateTimeOffset now)
    {
        if (!Frozen) throw new DomainException("The account isn't frozen.");
        return new AccountUnfrozen(Id, now);
    }

    public long OutgoingSince(DateTimeOffset since) => _outgoing.Where(o => o.At > since).Sum(o => o.Amount);

    private void CheckOutgoing(long amount, DateTimeOffset now)
    {
        EnsureOpen();
        if (amount <= 0) throw new DomainException("Amount must be positive.");
        if (amount > Balance) throw new DomainException("Insufficient funds.");
        if (OutgoingSince(now.AddDays(-1)) + amount > DailyOutgoingLimit)
            throw new DomainException("This would exceed the daily outgoing limit of 500,000.00.");
    }

    private void EnsureOpen()
    {
        if (Version < 0) throw new DomainException("Account not found.");
        if (Frozen) throw new DomainException("The account is frozen.");
    }
}
