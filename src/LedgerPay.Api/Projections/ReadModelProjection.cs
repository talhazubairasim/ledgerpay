using System.Text.Json;
using LedgerPay.Api.EventStore;
using LedgerPay.Domain;
using Npgsql;

namespace LedgerPay.Api.Projections;

/// <summary>Applies one event to the read models (balances, activity, ledger, transfers).</summary>
public static class ReadModelProjection
{
    public static async Task ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, StoredEvent stored)
    {
        switch (stored.Data)
        {
            case AccountOpened o:
                await Exec(conn, tx, "INSERT INTO rm_accounts (id, owner_id, number, name, currency, opened_at) VALUES ($1,$2,$3,$4,$5,$6) ON CONFLICT (id) DO NOTHING",
                    o.AccountId, o.OwnerId, o.Number, o.Name, o.Currency, o.At);
                break;
            case FundsDeposited d:
                await Money(conn, tx, stored, d.AccountId, d.Amount, "Deposit", string.IsNullOrEmpty(d.Reference) ? "Deposit" : $"Deposit · {d.Reference}");
                break;
            case FundsWithdrawn w:
                await Money(conn, tx, stored, w.AccountId, -w.Amount, "Withdrawal", string.IsNullOrEmpty(w.Reference) ? "Withdrawal" : $"Withdrawal · {w.Reference}");
                break;
            case TransferSent s:
                await Money(conn, tx, stored, s.AccountId, -s.Amount, "TransferOut", $"Transfer to {await NumberOf(conn, tx, s.ToAccountId)}{await MemoOf(conn, tx, s.TransferId)}");
                break;
            case TransferReceived r:
                await Money(conn, tx, stored, r.AccountId, r.Amount, "TransferIn", $"Transfer from {await NumberOf(conn, tx, r.FromAccountId)}{await MemoOf(conn, tx, r.TransferId)}");
                break;
            case AccountFrozen f:
                await Exec(conn, tx, "UPDATE rm_accounts SET frozen = true WHERE id = $1", f.AccountId);
                break;
            case AccountUnfrozen u:
                await Exec(conn, tx, "UPDATE rm_accounts SET frozen = false WHERE id = $1", u.AccountId);
                break;
            case TransferRequested t:
                await Exec(conn, tx, """
                    INSERT INTO rm_transfers (id, requested_by, from_account, to_account, amount, currency, memo, status, requested_at, updated_at)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,'Requested',$8,$8) ON CONFLICT (id) DO NOTHING
                    """, t.TransferId, t.RequestedBy, t.FromAccountId, t.ToAccountId, t.Amount, t.Currency, t.Memo, t.At);
                break;
            case TransferFlagged f:
                await Exec(conn, tx, "UPDATE rm_transfers SET status = 'PendingReview', reasons = $2::jsonb, score = $3, updated_at = $4 WHERE id = $1",
                    f.TransferId, JsonSerializer.Serialize(f.Reasons), f.Score, f.At);
                break;
            case TransferApproved a:
                await Exec(conn, tx, "UPDATE rm_transfers SET status = 'Approved', updated_at = $2 WHERE id = $1", a.TransferId, a.At);
                break;
            case TransferRejected x:
                await Exec(conn, tx, "UPDATE rm_transfers SET status = 'Rejected', rejection_reason = $2, updated_at = $3 WHERE id = $1", x.TransferId, x.Reason, x.At);
                break;
            case TransferCompleted c:
                await Exec(conn, tx, "UPDATE rm_transfers SET status = 'Completed', updated_at = $2 WHERE id = $1", c.TransferId, c.At);
                break;
        }
    }

    private static async Task Money(NpgsqlConnection conn, NpgsqlTransaction tx, StoredEvent stored, Guid accountId, long signed, string kind, string description)
    {
        await using var update = new NpgsqlCommand("UPDATE rm_accounts SET balance = balance + $2 WHERE id = $1 RETURNING balance, currency", conn, tx);
        update.Parameters.AddWithValue(accountId);
        update.Parameters.AddWithValue(signed);
        long balance;
        string currency;
        await using (var reader = await update.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new InvalidOperationException($"Projection: account {accountId} not found.");
            balance = reader.GetInt64(0);
            currency = reader.GetString(1);
        }

        await Exec(conn, tx, "INSERT INTO rm_activity (account_id, position, kind, amount, balance_after, description, at) VALUES ($1,$2,$3,$4,$5,$6,$7)",
            accountId, stored.Position, kind, signed, balance, description, stored.CreatedAt);

        foreach (var line in Ledger.Post(stored.Data, currency))
            await Exec(conn, tx, "INSERT INTO rm_ledger (position, account, currency, amount, at) VALUES ($1,$2,$3,$4,$5)",
                stored.Position, line.Account, currency, line.Amount, stored.CreatedAt);
    }

    private static async Task<string> NumberOf(NpgsqlConnection conn, NpgsqlTransaction tx, Guid accountId)
    {
        await using var cmd = new NpgsqlCommand("SELECT number FROM rm_accounts WHERE id = $1", conn, tx);
        cmd.Parameters.AddWithValue(accountId);
        return (await cmd.ExecuteScalarAsync()) as string ?? "unknown";
    }

    private static async Task<string> MemoOf(NpgsqlConnection conn, NpgsqlTransaction tx, Guid transferId)
    {
        await using var cmd = new NpgsqlCommand("SELECT memo FROM rm_transfers WHERE id = $1", conn, tx);
        cmd.Parameters.AddWithValue(transferId);
        return (await cmd.ExecuteScalarAsync()) is string memo && memo.Length > 0 ? $" · {memo}" : "";
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        await cmd.ExecuteNonQueryAsync();
    }
}
