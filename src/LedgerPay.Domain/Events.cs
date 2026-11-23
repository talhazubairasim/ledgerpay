namespace LedgerPay.Domain;

/// <summary>Marker for everything stored in the event store. Amounts are in minor units (cents/paisa).</summary>
public interface IEvent;

// ── Account (wallet) stream: "account-{id}" ─────────────────────────────────────
public record AccountOpened(Guid AccountId, Guid OwnerId, string Number, string Name, string Currency, DateTimeOffset At) : IEvent;
public record FundsDeposited(Guid AccountId, long Amount, string Reference, DateTimeOffset At) : IEvent;
public record FundsWithdrawn(Guid AccountId, long Amount, string Reference, DateTimeOffset At) : IEvent;
public record TransferSent(Guid AccountId, Guid TransferId, Guid ToAccountId, long Amount, DateTimeOffset At) : IEvent;
public record TransferReceived(Guid AccountId, Guid TransferId, Guid FromAccountId, long Amount, DateTimeOffset At) : IEvent;
public record AccountFrozen(Guid AccountId, string Reason, DateTimeOffset At) : IEvent;
public record AccountUnfrozen(Guid AccountId, DateTimeOffset At) : IEvent;

// ── Transfer stream: "transfer-{id}" ───────────────────────────────────────────
public record TransferRequested(Guid TransferId, Guid RequestedBy, Guid FromAccountId, Guid ToAccountId, long Amount, string Currency, string Memo, DateTimeOffset At) : IEvent;
public record TransferFlagged(Guid TransferId, IReadOnlyList<string> Reasons, int Score, DateTimeOffset At) : IEvent;
public record TransferApproved(Guid TransferId, Guid ReviewerId, DateTimeOffset At) : IEvent;
public record TransferRejected(Guid TransferId, string Reason, DateTimeOffset At) : IEvent;
public record TransferCompleted(Guid TransferId, DateTimeOffset At) : IEvent;

public static class Streams
{
    public static string Account(Guid id) => $"account-{id}";
    public static string Transfer(Guid id) => $"transfer-{id}";
}

/// <summary>A broken business rule (insufficient funds, frozen account, …).</summary>
public class DomainException(string message) : Exception(message);
