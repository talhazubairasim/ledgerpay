using LedgerPay.Api.EventStore;
using Npgsql;

namespace LedgerPay.Api.Projections;

/// <summary>
/// Tails the event log and updates the read models. Each batch and its checkpoint commit in one
/// transaction, so every event is applied exactly once. A Postgres advisory lock ensures only one
/// projector runs across all API replicas.
/// </summary>
public class Projector(NpgsqlDataSource db, EventStore.EventStore store)
{
    private const long LockId = 0x1ED6E2;
    private const string ReadModels = "rm_accounts, rm_activity, rm_ledger, rm_transfers";

    /// <summary>Applies everything currently visible. Returns how many events were applied (0 if another projector is running).</summary>
    public async Task<int> CatchUpAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        if (!await Scalar<bool>(conn, $"SELECT pg_try_advisory_lock({LockId})")) return 0;
        try { return await ApplyPendingAsync(conn, ct); }
        finally { await Scalar<bool>(conn, $"SELECT pg_advisory_unlock({LockId})"); }
    }

    /// <summary>Throws the read models away and replays the whole log into them, holding the projector lock throughout.</summary>
    public async Task<int> RebuildAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await Scalar<object>(conn, $"SELECT pg_advisory_lock({LockId})");
        try
        {
            await using (var cmd = new NpgsqlCommand(
                $"TRUNCATE {ReadModels} RESTART IDENTITY; UPDATE projection_checkpoint SET tx_id = '0'::xid8, position = 0 WHERE name = 'main';", conn))
                await cmd.ExecuteNonQueryAsync(ct);
            return await ApplyPendingAsync(conn, ct);
        }
        finally { await Scalar<bool>(conn, $"SELECT pg_advisory_unlock({LockId})"); }
    }

    private async Task<int> ApplyPendingAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var checkpoint = await ReadCheckpoint(conn, tx);
            var batch = await store.ReadAllAsync(checkpoint, 500, conn, tx);
            if (batch.Count == 0) { await tx.CommitAsync(ct); break; }
            foreach (var e in batch) await ReadModelProjection.ApplyAsync(conn, tx, e);
            var last = batch[^1];
            await using (var save = new NpgsqlCommand("UPDATE projection_checkpoint SET tx_id = $1::text::xid8, position = $2 WHERE name = 'main'", conn, tx))
            {
                save.Parameters.AddWithValue(last.TxId);
                save.Parameters.AddWithValue(last.Position);
                await save.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            total += batch.Count;
        }
        return total;
    }

    /// <summary>Read-your-writes: waits until the event at <paramref name="position"/> has been projected.</summary>
    public async Task<bool> WaitForAsync(long position, TimeSpan timeout, CancellationToken ct = default)
    {
        if (position <= 0) return true;
        var deadline = DateTime.UtcNow + timeout;
        await using var conn = await db.OpenConnectionAsync(ct);
        while (true)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM events e, projection_checkpoint c
                               WHERE e.position = $1 AND c.name = 'main' AND (c.tx_id, c.position) >= (e.tx_id, e.position))
                """, conn);
            cmd.Parameters.AddWithValue(position);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return true;
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(25, ct);
        }
    }

    private static async Task<Checkpoint> ReadCheckpoint(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        await using var cmd = new NpgsqlCommand("SELECT tx_id::text, position FROM projection_checkpoint WHERE name = 'main'", conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new Checkpoint(reader.GetString(0), reader.GetInt64(1)) : Checkpoint.Start;
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}

/// <summary>Runs the projector continuously (every 100 ms when idle).</summary>
public class ProjectorService(Projector projector, ILogger<ProjectorService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await projector.CatchUpAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Projection failed; retrying");
                await Task.Delay(1000, stoppingToken);
            }
            await Task.Delay(100, stoppingToken);
        }
    }
}
