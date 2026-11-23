namespace LedgerPay.Domain;

public enum TransferStatus { Requested, PendingReview, Approved, Completed, Rejected }

public class Transfer
{
    public Guid Id { get; private set; }
    public Guid RequestedBy { get; private set; }
    public Guid From { get; private set; }
    public Guid To { get; private set; }
    public long Amount { get; private set; }
    public string Currency { get; private set; } = "";
    public string Memo { get; private set; } = "";
    public TransferStatus Status { get; private set; }
    public IReadOnlyList<string> Reasons { get; private set; } = [];
    public string? RejectionReason { get; private set; }
    public int Version { get; private set; } = -1;

    public static Transfer Replay(IEnumerable<IEvent> events)
    {
        var t = new Transfer();
        foreach (var e in events) t.Apply(e);
        return t;
    }

    public void Apply(IEvent e)
    {
        switch (e)
        {
            case TransferRequested r:
                Id = r.TransferId; RequestedBy = r.RequestedBy; From = r.FromAccountId; To = r.ToAccountId;
                Amount = r.Amount; Currency = r.Currency; Memo = r.Memo; Status = TransferStatus.Requested; break;
            case TransferFlagged f: Status = TransferStatus.PendingReview; Reasons = f.Reasons; break;
            case TransferApproved: Status = TransferStatus.Approved; break;
            case TransferRejected x: Status = TransferStatus.Rejected; RejectionReason = x.Reason; break;
            case TransferCompleted: Status = TransferStatus.Completed; break;
        }
        Version++;
    }

    public static TransferRequested Request(Guid id, Guid requestedBy, Account from, Account to, long amount, string memo, DateTimeOffset now)
    {
        if (from.Id == to.Id) throw new DomainException("Choose a different wallet to send to.");
        if (from.Currency != to.Currency) throw new DomainException($"Both wallets must use the same currency ({from.Currency} → {to.Currency}).");
        if (amount <= 0) throw new DomainException("Amount must be positive.");
        return new TransferRequested(id, requestedBy, from.Id, to.Id, amount, from.Currency, memo.Trim(), now);
    }

    public TransferFlagged Flag(FraudAssessment assessment, DateTimeOffset now)
    {
        Require(TransferStatus.Requested, "flag");
        return new TransferFlagged(Id, assessment.Reasons, assessment.Score, now);
    }

    public TransferApproved Approve(Guid reviewer, DateTimeOffset now)
    {
        Require(TransferStatus.PendingReview, "approve");
        if (reviewer == RequestedBy) throw new DomainException("You can't approve your own transfer.");
        return new TransferApproved(Id, reviewer, now);
    }

    public TransferRejected Reject(string reason, DateTimeOffset now)
    {
        if (Status is not (TransferStatus.Requested or TransferStatus.PendingReview or TransferStatus.Approved))
            throw new DomainException($"A {Status} transfer can't be rejected.");
        return new TransferRejected(Id, reason, now);
    }

    public TransferCompleted Complete(DateTimeOffset now)
    {
        if (Status is not (TransferStatus.Requested or TransferStatus.Approved))
            throw new DomainException($"A {Status} transfer can't be completed.");
        return new TransferCompleted(Id, now);
    }

    private void Require(TransferStatus expected, string action)
    {
        if (Status != expected) throw new DomainException($"Can't {action} a {Status} transfer.");
    }
}
