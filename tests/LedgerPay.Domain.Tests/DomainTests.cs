using LedgerPay.Domain;

namespace LedgerPay.Domain.Tests;

public class AccountTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 7, 12, 0, 0, TimeSpan.Zero);

    internal static Account Wallet(long balance = 0, string currency = "PKR", Guid? owner = null)
    {
        var opened = Account.Open(owner ?? Guid.NewGuid(), "Main", currency, "LP0000000001", Now.AddDays(-1));
        var a = Account.Replay([opened]);
        if (balance > 0) a.Apply(a.Deposit(balance, "seed", Now.AddDays(-1)));
        return a;
    }

    [Fact]
    public void Replaying_events_rebuilds_balance_and_version()
    {
        var a = Wallet(10_000);
        a.Apply(a.Withdraw(2_500, "atm", Now));
        a.Apply(a.Send(Guid.NewGuid(), Guid.NewGuid(), 1_000, Now));
        a.Apply(a.Receive(Guid.NewGuid(), Guid.NewGuid(), 500, Now));
        Assert.Equal(7_000, a.Balance);
        Assert.Equal(4, a.Version);
        Assert.Equal(3_500, a.OutgoingSince(Now.AddDays(-1)));
    }

    [Fact]
    public void Outgoing_money_needs_funds_an_open_account_and_room_under_the_daily_limit()
    {
        var a = Wallet(Account.DailyOutgoingLimit + 10_000);
        Assert.Equal("Insufficient funds.", Assert.Throws<DomainException>(() => a.Withdraw(Account.DailyOutgoingLimit + 20_000, "", Now)).Message);
        Assert.Throws<DomainException>(() => a.Withdraw(0, "", Now));
        a.Apply(a.Withdraw(Account.DailyOutgoingLimit - 100, "", Now));
        Assert.Contains("daily", Assert.Throws<DomainException>(() => a.Send(Guid.NewGuid(), Guid.NewGuid(), 200, Now)).Message);
        a.Apply(a.Send(Guid.NewGuid(), Guid.NewGuid(), 200, Now.AddDays(1).AddMinutes(1)));   // window has rolled

        a.Apply(a.Freeze("Suspicious", Now));
        Assert.Throws<DomainException>(() => a.Deposit(1, "", Now));
        Assert.Throws<DomainException>(() => a.Receive(Guid.NewGuid(), Guid.NewGuid(), 1, Now));
        Assert.Throws<DomainException>(() => a.Freeze("again", Now));
        a.Apply(a.Unfreeze(Now));
        Assert.Throws<DomainException>(() => a.Unfreeze(Now));
        a.Apply(a.Deposit(1, "", Now));
    }

    [Fact]
    public void Opening_validates_currency_and_deposits_are_capped()
    {
        Assert.Throws<DomainException>(() => Account.Open(Guid.NewGuid(), "x", "GBP", "LP1", Now));
        Assert.Throws<DomainException>(() => Account.Open(Guid.NewGuid(), " ", "PKR", "LP1", Now));
        Assert.Throws<DomainException>(() => Wallet().Deposit(Account.MaxDeposit + 1, "", Now));
        Assert.Throws<DomainException>(() => new Account().Deposit(1, "", Now));   // never opened
    }

    [Theory]
    [InlineData("12.34", 1234)]
    [InlineData("0.5", 50)]
    [InlineData("100", 10000)]
    public void Amounts_convert_to_minor_units(string major, long minor)
    {
        Assert.Equal(minor, Amounts.ToMinor(decimal.Parse(major, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(decimal.Parse(major, System.Globalization.CultureInfo.InvariantCulture), Amounts.ToMajor(minor));
    }

    [Fact]
    public void Amounts_reject_fractions_of_a_cent() => Assert.Throws<DomainException>(() => Amounts.ToMinor(1.005m));
}

public class TransferTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Requests_need_distinct_wallets_in_the_same_currency()
    {
        var a = AccountTests.Wallet(1000);
        var usd = AccountTests.Wallet(0, "USD");
        Assert.Throws<DomainException>(() => Transfer.Request(Guid.NewGuid(), a.OwnerId, a, a, 10, "", Now));
        Assert.Contains("currency", Assert.Throws<DomainException>(() => Transfer.Request(Guid.NewGuid(), a.OwnerId, a, usd, 10, "", Now)).Message);
        Assert.Throws<DomainException>(() => Transfer.Request(Guid.NewGuid(), a.OwnerId, a, AccountTests.Wallet(), 0, "", Now));
    }

    [Fact]
    public void Review_lifecycle()
    {
        var a = AccountTests.Wallet(1000);
        var requested = Transfer.Request(Guid.NewGuid(), a.OwnerId, a, AccountTests.Wallet(), 10, " rent ", Now);
        var t = Transfer.Replay([requested]);
        Assert.Equal("rent", t.Memo);
        Assert.Throws<DomainException>(() => t.Approve(Guid.NewGuid(), Now));            // not flagged yet
        t.Apply(t.Flag(new FraudAssessment(60, ["Large amount"]), Now));
        Assert.Equal(TransferStatus.PendingReview, t.Status);
        Assert.Throws<DomainException>(() => t.Complete(Now));                            // must be approved first
        Assert.Throws<DomainException>(() => t.Approve(a.OwnerId, Now));                 // no self-approval
        t.Apply(t.Approve(Guid.NewGuid(), Now));
        t.Apply(t.Complete(Now));
        Assert.Equal(TransferStatus.Completed, t.Status);
        Assert.Throws<DomainException>(() => t.Reject("late", Now));
        Assert.Equal(3, t.Version);
    }
}

public class FraudTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Small_transfers_to_known_payees_pass()
    {
        var a = AccountTests.Wallet(1_000_000);
        var payee = Guid.NewGuid();
        a.Apply(a.Send(Guid.NewGuid(), payee, 100, Now.AddHours(-1)));
        var result = FraudEngine.Default.Assess(new FraudContext(a, payee, 300_000, Now));
        Assert.False(result.NeedsReview);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Large_amounts_and_velocity_are_flagged()
    {
        var a = AccountTests.Wallet(5_000_000);
        var large = FraudEngine.Default.Assess(new FraudContext(a, Guid.NewGuid(), 1_500_000, Now));
        Assert.True(large.NeedsReview);
        Assert.Equal(100, large.Score);   // large 60 + new payee 40, capped at 100
        Assert.Equal(2, large.Reasons.Count);

        for (int i = 0; i < 5; i++) a.Apply(a.Send(Guid.NewGuid(), Guid.NewGuid(), 10, Now.AddMinutes(-i)));
        var fast = FraudEngine.Default.Assess(new FraudContext(a, Guid.NewGuid(), 10, Now));
        Assert.True(fast.NeedsReview);
        Assert.Contains(fast.Reasons, r => r.Contains("minutes"));
        Assert.False(FraudEngine.Default.Assess(new FraudContext(a, Guid.NewGuid(), 10, Now.AddMinutes(30))).NeedsReview);
    }

    [Fact]
    public void Draining_a_wallet_to_a_new_payee_adds_up()
    {
        var a = AccountTests.Wallet(250_000);
        var r = FraudEngine.Default.Assess(new FraudContext(a, Guid.NewGuid(), 240_000, Now));
        Assert.Equal(60, r.Score);   // new payee 40 + drain 20
        Assert.True(r.NeedsReview);
    }
}

public class LedgerTests
{
    [Fact]
    public void Every_money_event_posts_balanced_lines()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UnixEpoch;
        IEvent[] events =
        [
            new FundsDeposited(id, 500, "", now), new FundsWithdrawn(id, 200, "", now),
            new TransferSent(id, Guid.NewGuid(), Guid.NewGuid(), 50, now), new TransferReceived(id, Guid.NewGuid(), Guid.NewGuid(), 50, now),
        ];
        foreach (var e in events) Assert.Equal(0, Ledger.Post(e, "PKR").Sum(l => l.Amount));
        var all = events.SelectMany(e => Ledger.Post(e, "PKR")).ToList();
        Assert.Equal(300, all.Where(l => l.Account == Ledger.Wallet(id)).Sum(l => l.Amount));
        Assert.Equal(0, all.Where(l => l.Account == Ledger.Clearing("PKR")).Sum(l => l.Amount));
        Assert.Empty(Ledger.Post(new AccountFrozen(id, "", now), "PKR"));
    }

    [Fact]
    public void Random_histories_keep_the_books_balanced_and_wallets_matching()
    {
        var rng = new Random(42);
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var wallets = Enumerable.Range(0, 4).Select(_ => AccountTests.Wallet()).ToList();
        var lines = new List<LedgerLine>();
        void Record(Account a, IEvent e) { a.Apply(e); lines.AddRange(Ledger.Post(e, "PKR")); }

        for (int i = 0; i < 2_000; i++)
        {
            now = now.AddMinutes(30);
            var a = wallets[rng.Next(4)];
            var amount = rng.Next(1, 50_000);
            try
            {
                switch (rng.Next(3))
                {
                    case 0: Record(a, a.Deposit(amount, "", now)); break;
                    case 1: Record(a, a.Withdraw(amount, "", now)); break;
                    default:
                        var b = wallets[rng.Next(4)];
                        if (b == a) break;
                        var t = Guid.NewGuid();
                        var sent = a.Send(t, b.Id, amount, now);
                        Record(a, sent);
                        Record(b, b.Receive(t, a.Id, amount, now));
                        break;
                }
            }
            catch (DomainException) { /* insufficient funds etc. — nothing recorded */ }
        }

        Assert.Equal(0, lines.Sum(l => l.Amount));
        foreach (var w in wallets)
        {
            Assert.True(w.Balance >= 0);
            Assert.Equal(w.Balance, lines.Where(l => l.Account == Ledger.Wallet(w.Id)).Sum(l => l.Amount));
        }
    }
}
