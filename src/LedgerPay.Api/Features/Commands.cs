using System.Security.Cryptography;
using System.Text;
using LedgerPay.Api.EventStore;
using LedgerPay.Domain;

namespace LedgerPay.Api.Features;

public record CommandResult<T>(T Value, long Position);

/// <summary>Write side for wallets: load from events, decide, append with the expected version.</summary>
public class AccountService(EventStore.EventStore store, TimeProvider clock)
{
    public const int MaxWalletsPerUser = 5;

    public async Task<Account> LoadAsync(Guid id)
    {
        var events = await store.ReadStreamAsync(Streams.Account(id));
        return Account.Replay(events.Select(e => e.Data));
    }

    public async Task<CommandResult<AccountOpened>> OpenAsync(Guid owner, string name, string currency)
    {
        var number = "LP" + RandomNumberGenerator.GetInt32(0, 1_000_000_000).ToString("D9") + RandomNumberGenerator.GetInt32(0, 10);
        var opened = Account.Open(owner, name, currency, number, clock.GetUtcNow());
        var position = await store.AppendAsync(new AppendRequest(Streams.Account(opened.AccountId), -1, [opened]));
        return new(opened, position);
    }

    public Task<CommandResult<Account>> DepositAsync(Guid id, long amount, string reference) =>
        Change(id, a => a.Deposit(amount, reference, clock.GetUtcNow()));

    public Task<CommandResult<Account>> WithdrawAsync(Guid id, long amount, string reference) =>
        Change(id, a => a.Withdraw(amount, reference, clock.GetUtcNow()));

    public Task<CommandResult<Account>> FreezeAsync(Guid id, string reason) => Change(id, a => a.Freeze(reason, clock.GetUtcNow()));

    public Task<CommandResult<Account>> UnfreezeAsync(Guid id) => Change(id, a => a.Unfreeze(clock.GetUtcNow()));

    private async Task<CommandResult<Account>> Change(Guid id, Func<Account, IEvent> decide)
    {
        return await Retry.OnConflict(async () =>
        {
            var account = await LoadAsync(id);
            var e = decide(account);
            var position = await store.AppendAsync(new AppendRequest(Streams.Account(id), account.Version, [e]));
            account.Apply(e);
            return new CommandResult<Account>(account, position);
        });
    }
}

public static class Retry
{
    /// <summary>Optimistic concurrency: reload and decide again when another writer got there first.</summary>
    public static async Task<T> OnConflict<T>(Func<Task<T>> attempt, int times = 8)
    {
        for (var i = 1; ; i++)
        {
            try { return await attempt(); }
            catch (ConcurrencyException) when (i < times) { await Task.Delay(Random.Shared.Next(5, 20 * i)); }
        }
    }
}

public record TransferView(Guid Id, TransferStatus Status, long Amount, string Currency, IReadOnlyList<string> Reasons, string? RejectionReason, bool Replayed = false);

/// <summary>
/// Transfers: idempotent by (user, Idempotency-Key), screened by the fraud engine, and executed by
/// appending the debit, the credit and the completion to three streams in a single transaction.
/// </summary>
public class TransferService(EventStore.EventStore store, AccountService accounts, FraudEngine fraud, TimeProvider clock)
{
    /// <summary>The same user and key always map to the same transfer stream.</summary>
    public static Guid IdFor(Guid user, string idempotencyKey) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"{user:N}:{idempotencyKey}")));

    public async Task<Transfer> LoadAsync(Guid id) =>
        Transfer.Replay((await store.ReadStreamAsync(Streams.Transfer(id))).Select(e => e.Data));

    public async Task<CommandResult<TransferView>> RequestAsync(Guid user, string idempotencyKey, Guid fromId, Guid toId, long amount, string memo)
    {
        var id = IdFor(user, idempotencyKey);
        return await Retry.OnConflict(async () =>
        {
            var existing = await LoadAsync(id);
            if (existing.Version >= 0)
            {
                if (existing.From != fromId || existing.To != toId || existing.Amount != amount)
                    throw new DomainException("This Idempotency-Key was already used for a different transfer.");
                return new CommandResult<TransferView>(View(existing) with { Replayed = true }, 0);
            }

            var from = await accounts.LoadAsync(fromId);
            if (from.Version < 0 || from.OwnerId != user) throw new DomainException("Account not found.");
            var to = await accounts.LoadAsync(toId);
            if (to.Version < 0) throw new DomainException("Payee account not found.");

            var now = clock.GetUtcNow();
            var requested = Transfer.Request(id, user, from, to, amount, memo, now);
            var transfer = Transfer.Replay([requested]);

            var assessment = fraud.Assess(new FraudContext(from, to.Id, amount, now));
            if (assessment.NeedsReview)
            {
                var flagged = transfer.Flag(assessment, now);
                var position = await store.AppendAsync(new AppendRequest(Streams.Transfer(id), -1, [requested, flagged]));
                transfer.Apply(flagged);
                return new CommandResult<TransferView>(View(transfer), position);
            }

            return await ExecuteAsync(transfer, from, to, -1, [requested]);
        });
    }

    public async Task<CommandResult<TransferView>> ApproveAsync(Guid id, Guid reviewer) =>
        await Retry.OnConflict(async () =>
        {
            var transfer = await LoadAsync(id);
            if (transfer.Version < 0) throw new KeyNotFoundException();
            var approved = transfer.Approve(reviewer, clock.GetUtcNow());
            var expected = transfer.Version;
            transfer.Apply(approved);
            var from = await accounts.LoadAsync(transfer.From);
            var to = await accounts.LoadAsync(transfer.To);
            return await ExecuteAsync(transfer, from, to, expected, [approved]);
        });

    public async Task<CommandResult<TransferView>> RejectAsync(Guid id, string reason) =>
        await Retry.OnConflict(async () =>
        {
            var transfer = await LoadAsync(id);
            if (transfer.Version < 0) throw new KeyNotFoundException();
            var rejected = transfer.Reject(reason, clock.GetUtcNow());
            var position = await store.AppendAsync(new AppendRequest(Streams.Transfer(id), transfer.Version, [rejected]));
            transfer.Apply(rejected);
            return new CommandResult<TransferView>(View(transfer), position);
        });

    /// <summary>Debit, credit and complete atomically; a failed business check is recorded as a rejection.</summary>
    private async Task<CommandResult<TransferView>> ExecuteAsync(Transfer transfer, Account from, Account to, int expectedTransferVersion, List<IEvent> transferEvents)
    {
        var now = clock.GetUtcNow();
        try
        {
            var sent = from.Send(transfer.Id, to.Id, transfer.Amount, now);
            var received = to.Receive(transfer.Id, from.Id, transfer.Amount, now);
            var completed = transfer.Complete(now);
            transferEvents.Add(completed);
            var position = await store.AppendAsync(
                new AppendRequest(Streams.Transfer(transfer.Id), expectedTransferVersion, transferEvents),
                new AppendRequest(Streams.Account(from.Id), from.Version, [sent]),
                new AppendRequest(Streams.Account(to.Id), to.Version, [received]));
            transfer.Apply(completed);
            return new CommandResult<TransferView>(View(transfer), position);
        }
        catch (DomainException e)
        {
            var rejected = transfer.Reject(e.Message, now);
            transferEvents.RemoveAll(x => x is TransferCompleted);
            transferEvents.Add(rejected);
            var position = await store.AppendAsync(new AppendRequest(Streams.Transfer(transfer.Id), expectedTransferVersion, transferEvents));
            transfer.Apply(rejected);
            return new CommandResult<TransferView>(View(transfer), position);
        }
    }

    public static TransferView View(Transfer t) => new(t.Id, t.Status, t.Amount, t.Currency, t.Reasons, t.RejectionReason);
}
