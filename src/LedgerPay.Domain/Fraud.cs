namespace LedgerPay.Domain;

public record FraudContext(Account From, Guid To, long Amount, DateTimeOffset Now);

public record FraudAssessment(int Score, IReadOnlyList<string> Reasons)
{
    /// <summary>Transfers scoring this much or more wait for a reviewer.</summary>
    public const int ReviewThreshold = 50;
    public bool NeedsReview => Score >= ReviewThreshold;
}

public interface IFraudRule
{
    (int Score, string Reason)? Evaluate(FraudContext ctx);
}

/// <summary>Anything from 10,000.00 up.</summary>
public class LargeAmountRule(long threshold = 1_000_000) : IFraudRule
{
    public (int, string)? Evaluate(FraudContext c) =>
        c.Amount >= threshold ? (60, $"Large amount (≥ {threshold / 100m:N2})") : null;
}

/// <summary>Five or more transfers out of the wallet in the last ten minutes.</summary>
public class VelocityRule(int maxTransfers = 5, int minutes = 10) : IFraudRule
{
    public (int, string)? Evaluate(FraudContext c) =>
        c.From.Sent.Count(s => s.At > c.Now.AddMinutes(-minutes)) >= maxTransfers
            ? (50, $"{maxTransfers}+ transfers in {minutes} minutes") : null;
}

/// <summary>The first payment to a payee is worth a look when it isn't small.</summary>
public class NewPayeeRule(long threshold = 200_000) : IFraudRule
{
    public (int, string)? Evaluate(FraudContext c) =>
        c.Amount >= threshold && c.From.Sent.All(s => s.To != c.To)
            ? (40, $"First transfer to this payee is ≥ {threshold / 100m:N2}") : null;
}

/// <summary>Moving most of the wallet out at once.</summary>
public class DrainRule(decimal share = 0.9m, long minimum = 100_000) : IFraudRule
{
    public (int, string)? Evaluate(FraudContext c) =>
        c.Amount >= minimum && c.From.Balance > 0 && c.Amount >= c.From.Balance * share
            ? (20, $"Moves {share:P0}+ of the balance") : null;
}

public class FraudEngine(IEnumerable<IFraudRule> rules)
{
    public static FraudEngine Default => new([new LargeAmountRule(), new VelocityRule(), new NewPayeeRule(), new DrainRule()]);

    public FraudAssessment Assess(FraudContext ctx)
    {
        var hits = rules.Select(r => r.Evaluate(ctx)).Where(h => h is not null).Select(h => h!.Value).ToList();
        return new FraudAssessment(Math.Min(100, hits.Sum(h => h.Score)), hits.Select(h => h.Reason).ToList());
    }
}
