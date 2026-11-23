using System.Text.Json;
using LedgerPay.Domain;
using Npgsql;

namespace LedgerPay.Api.EventStore;

public record StoredEvent(long Position, string TxId, string StreamId, int Version, IEvent Data, DateTimeOffset CreatedAt)
{
    public string Type => Data.GetType().Name;
}

/// <summary>Append to one stream, expecting it to be at <paramref name="ExpectedVersion"/> (-1 = new stream).</summary>
public record AppendRequest(string StreamId, int ExpectedVersion, IReadOnlyList<IEvent> Events);

/// <summary>Someone else wrote to the stream first; reload and retry.</summary>
public class ConcurrencyException(string stream) : Exception($"Stream {stream} was modified concurrently.")
{
    public string Stream { get; } = stream;
}

/// <summary>Projection checkpoint: the (transaction, position) of the last event applied.</summary>
public readonly record struct Checkpoint(string TxId, long Position)
{
    public static readonly Checkpoint Start = new("0", 0);
}

/// <summary>
/// A Postgres event store. Appends to several streams happen in one transaction, each guarded by
/// its expected version, so a transfer's debit, credit and status change commit together or not at all.
/// </summary>
public class EventStore(NpgsqlDataSource db)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, Type> Types = typeof(IEvent).Assembly.GetTypes()
        .Where(t => typeof(IEvent).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
        .ToDictionary(t => t.Name);

    public async Task<long> AppendAsync(params AppendRequest[] appends)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        long position = 0;
        foreach (var append in appends)
        {
            await using (var current = new NpgsqlCommand("SELECT COALESCE(MAX(version), -1) FROM events WHERE stream_id = $1", conn, tx))
            {
                current.Parameters.AddWithValue(append.StreamId);
                if ((int)(await current.ExecuteScalarAsync())! != append.ExpectedVersion)
                    throw new ConcurrencyException(append.StreamId);
            }

            var version = append.ExpectedVersion;
            foreach (var e in append.Events)
            {
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO events (stream_id, version, type, data) VALUES ($1, $2, $3, $4::jsonb) RETURNING position", conn, tx);
                insert.Parameters.AddWithValue(append.StreamId);
                insert.Parameters.AddWithValue(++version);
                insert.Parameters.AddWithValue(e.GetType().Name);
                insert.Parameters.AddWithValue(JsonSerializer.Serialize(e, e.GetType(), Json));
                try
                {
                    position = (long)(await insert.ExecuteScalarAsync())!;
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    throw new ConcurrencyException(append.StreamId);
                }
            }
        }
        await tx.CommitAsync();
        return position;
    }

    public async Task<IReadOnlyList<StoredEvent>> ReadStreamAsync(string streamId)
    {
        await using var cmd = db.CreateCommand(
            "SELECT position, tx_id::text, stream_id, version, type, data::text, created_at FROM events WHERE stream_id = $1 ORDER BY version");
        cmd.Parameters.AddWithValue(streamId);
        return await ReadAsync(cmd);
    }

    /// <summary>
    /// Events after the checkpoint, in commit-safe order. Only transactions older than every
    /// in-flight transaction are returned, so a projector never skips an event that commits later
    /// with a lower position (bigserial values are handed out before commit).
    /// </summary>
    public async Task<IReadOnlyList<StoredEvent>> ReadAllAsync(Checkpoint after, int limit, NpgsqlConnection conn, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT position, tx_id::text, stream_id, version, type, data::text, created_at FROM events
            WHERE (tx_id, position) > ($1::text::xid8, $2)
              AND tx_id < pg_snapshot_xmin(pg_current_snapshot())
            ORDER BY tx_id, position
            LIMIT $3
            """, conn, tx);
        cmd.Parameters.AddWithValue(after.TxId);
        cmd.Parameters.AddWithValue(after.Position);
        cmd.Parameters.AddWithValue(limit);
        return await ReadAsync(cmd);
    }

    /// <summary>Raw page of the global log for the admin event browser.</summary>
    public async Task<IReadOnlyList<StoredEvent>> ReadGlobalAsync(long afterPosition, int limit)
    {
        await using var cmd = db.CreateCommand(
            "SELECT position, tx_id::text, stream_id, version, type, data::text, created_at FROM events WHERE position > $1 ORDER BY position LIMIT $2");
        cmd.Parameters.AddWithValue(afterPosition);
        cmd.Parameters.AddWithValue(limit);
        return await ReadAsync(cmd);
    }

    private static async Task<IReadOnlyList<StoredEvent>> ReadAsync(NpgsqlCommand cmd)
    {
        var list = new List<StoredEvent>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var type = Types[reader.GetString(4)];
            var data = (IEvent)JsonSerializer.Deserialize(reader.GetString(5), type, Json)!;
            list.Add(new StoredEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), data,
                reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return list;
    }

    public static async Task InitialiseAsync(NpgsqlDataSource db, ILogger log)
    {
        using var stream = typeof(EventStore).Assembly.GetManifestResourceStream("LedgerPay.Api.EventStore.schema.sql")!;
        var sql = await new StreamReader(stream).ReadToEndAsync();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var cmd = db.CreateCommand(sql);
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (NpgsqlException e) when (attempt < 15)
            {
                log.LogWarning("Database not ready ({Message}); retrying", e.Message);
                await Task.Delay(2000);
            }
        }
    }
}
