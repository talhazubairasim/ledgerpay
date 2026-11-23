using System.Text.Json;
using Npgsql;

namespace LedgerPay.Api.Features;

public record AccountView(Guid Id, Guid OwnerId, string Number, string Name, string Currency, decimal Balance, bool Frozen, DateTimeOffset OpenedAt);
public record ActivityView(long Id, long Position, string Kind, decimal Amount, decimal BalanceAfter, string Description, DateTimeOffset At);
public record PayeeView(Guid Id, string Number, string Name, string Currency, string Owner);
public record TransferRow(Guid Id, Guid RequestedBy, Guid FromAccount, string FromNumber, Guid ToAccount, string ToNumber, decimal Amount, string Currency,
    string Memo, string Status, IReadOnlyList<string> Reasons, int Score, string? RejectionReason, DateTimeOffset RequestedAt, DateTimeOffset UpdatedAt);
public record TrialBalance(IReadOnlyList<TrialBalanceLine> Lines, bool Balanced, int WalletsChecked, IReadOnlyList<string> Mismatches);
public record TrialBalanceLine(string Currency, string Kind, decimal Total);

/// <summary>Read side: plain SQL over the projected tables.</summary>
public class ReadModels(NpgsqlDataSource db)
{
    private const string AccountColumns = "id, owner_id, number, name, currency, balance, frozen, opened_at";
    private const string TransferSelect = """
        SELECT t.id, t.requested_by, t.from_account, COALESCE(f.number, '?'), t.to_account, COALESCE(d.number, '?'), t.amount, t.currency,
               t.memo, t.status, t.reasons::text, t.score, t.rejection_reason, t.requested_at, t.updated_at
        FROM rm_transfers t LEFT JOIN rm_accounts f ON f.id = t.from_account LEFT JOIN rm_accounts d ON d.id = t.to_account
        """;

    public Task<List<AccountView>> AccountsOfAsync(Guid owner) =>
        List($"SELECT {AccountColumns} FROM rm_accounts WHERE owner_id = $1 ORDER BY opened_at", ReadAccount, owner);

    public async Task<AccountView?> AccountAsync(Guid id) =>
        (await List($"SELECT {AccountColumns} FROM rm_accounts WHERE id = $1", ReadAccount, id)).FirstOrDefault();

    public async Task<PayeeView?> PayeeAsync(string number) =>
        (await List("SELECT a.id, a.number, a.name, a.currency, COALESCE(u.full_name, '') FROM rm_accounts a LEFT JOIN users u ON u.id = a.owner_id WHERE a.number = $1",
            r => new PayeeView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), number.Trim().ToUpperInvariant())).FirstOrDefault();

    public Task<List<ActivityView>> ActivityAsync(Guid account, int take) =>
        List("SELECT id, position, kind, amount, balance_after, description, at FROM rm_activity WHERE account_id = $1 ORDER BY id DESC LIMIT $2",
            r => new ActivityView(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3) / 100m, r.GetInt64(4) / 100m, r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)),
            account, take);

    public Task<List<TransferRow>> TransfersForUserAsync(Guid user) =>
        List($"{TransferSelect} WHERE t.requested_by = $1 OR f.owner_id = $1 OR d.owner_id = $1 ORDER BY t.requested_at DESC LIMIT 200", ReadTransfer, user);

    public Task<List<TransferRow>> TransfersByStatusAsync(string status) =>
        List($"{TransferSelect} WHERE t.status = $1 ORDER BY t.requested_at", ReadTransfer, status);

    /// <summary>
    /// Double-entry check: every currency's ledger sums to zero, clearing accounts are empty,
    /// and each wallet's ledger total equals its projected balance.
    /// </summary>
    public async Task<TrialBalance> TrialBalanceAsync()
    {
        var lines = await List("""
            SELECT currency, split_part(account, ':', 1) AS kind, SUM(amount) FROM rm_ledger GROUP BY currency, kind
            UNION ALL SELECT currency, 'total', SUM(amount) FROM rm_ledger GROUP BY currency
            ORDER BY 1, 2
            """, r => new TrialBalanceLine(r.GetString(0), r.GetString(1), (long)r.GetDecimal(2) / 100m));
        var mismatches = await List("""
            SELECT a.number FROM rm_accounts a
            LEFT JOIN (SELECT account, SUM(amount) AS total FROM rm_ledger GROUP BY account) l ON l.account = 'wallet:' || a.id
            WHERE COALESCE(l.total, 0) <> a.balance OR a.balance < 0
            """, r => r.GetString(0));
        var wallets = (await List("SELECT count(*) FROM rm_accounts", r => r.GetInt64(0)))[0];
        var balanced = lines.Where(l => l.Kind is "total" or "clearing").All(l => l.Total == 0) && mismatches.Count == 0;
        return new TrialBalance(lines, balanced, (int)wallets, mismatches);
    }

    private static AccountView ReadAccount(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5) / 100m, r.GetBoolean(6), r.GetFieldValue<DateTimeOffset>(7));

    private static TransferRow ReadTransfer(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.GetInt64(6) / 100m, r.GetString(7),
            r.GetString(8), r.GetString(9), JsonSerializer.Deserialize<List<string>>(r.GetString(10)) ?? [], r.GetInt32(11),
            r.IsDBNull(12) ? null : r.GetString(12), r.GetFieldValue<DateTimeOffset>(13), r.GetFieldValue<DateTimeOffset>(14));

    private async Task<List<T>> List<T>(string sql, Func<NpgsqlDataReader, T> map, params object[] args)
    {
        await using var cmd = db.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await reader.ReadAsync()) list.Add(map(reader));
        return list;
    }
}
