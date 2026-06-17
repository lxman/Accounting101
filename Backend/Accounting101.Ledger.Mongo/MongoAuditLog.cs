using System.Security.Cryptography;
using System.Text;
using Accounting101.Ledger.Mongo.Documents;
using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Append-only, hash-chained audit log. Records every mutation with a point-in-time snapshot
/// of the principal that performed it. The engine does not authorize — it records what the
/// (already-authorized) actor did. Each client's records form an independently verifiable chain.
/// </summary>
public sealed class MongoAuditLog
{
    private readonly IMongoCollection<AuditRecordDocument> _audit;

    static MongoAuditLog() => LedgerMongoBootstrap.RegisterOnce();

    public MongoAuditLog(IMongoDatabase database, string collectionName = "audit")
    {
        ArgumentNullException.ThrowIfNull(database);
        _audit = database.GetCollection<AuditRecordDocument>(collectionName);
    }

    private const int MaxAppendAttempts = 64;

    /// <summary>
    /// Append a record to the client's chain. The per-client sequence is a linear, tamper-evident
    /// structure, so it is intentionally serialized: under concurrency two appends may compute the same
    /// sequence, the <see cref="EnsureIndexesAsync">unique index</see> rejects the loser with a
    /// duplicate key, and this method re-reads the tail and re-chains. A silent fork becomes a safe retry.
    /// </summary>
    public async Task AppendAsync(
        Guid clientId,
        Guid? entryId,
        int entryVersion,
        AuditAction action,
        Actor actor,
        string? reason,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        long atMs = at.ToUnixTimeMilliseconds();
        ActorSnapshot snapshot = ToSnapshot(actor);

        for (int attempt = 1; ; attempt++)
        {
            AuditRecordDocument? latest = await _audit
                .Find(a => a.ClientId == clientId)
                .SortByDescending(a => a.Sequence)
                .FirstOrDefaultAsync(cancellationToken);

            AuditRecordDocument record = new()
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                Sequence = (latest?.Sequence ?? 0) + 1,
                EntryId = entryId,
                EntryVersion = entryVersion,
                Action = action,
                Actor = snapshot,
                At = DateTimeOffset.FromUnixTimeMilliseconds(atMs).UtcDateTime, // ms precision — matches the hash on read-back
                Reason = reason,
                PreviousHash = latest?.Hash ?? string.Empty,
            };
            record.Hash = ComputeHash(record);

            try
            {
                await _audit.InsertOneAsync(record, cancellationToken: cancellationToken);
                return;
            }
            catch (MongoWriteException ex)
                when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey && attempt < MaxAppendAttempts)
            {
                // A concurrent append took this sequence; loop to re-read the tail and re-chain.
            }
        }
    }

    /// <summary>
    /// Creates the unique per-client sequence index that keeps the hash chain a single linear sequence
    /// under concurrent appends (the retry in <see cref="AppendAsync"/> relies on it). Idempotent.
    /// </summary>
    public Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        IndexKeysDefinition<AuditRecordDocument> keys = Builders<AuditRecordDocument>.IndexKeys
            .Ascending(a => a.ClientId)
            .Ascending(a => a.Sequence);

        return _audit.Indexes.CreateOneAsync(
            new CreateIndexModel<AuditRecordDocument>(keys, new CreateIndexOptions { Name = "client_sequence_unique", Unique = true }),
            cancellationToken: cancellationToken);
    }

    /// <summary>The action timeline for one entry — the "what happened to this entry" look-back.</summary>
    public async Task<IReadOnlyList<AuditRecordDocument>> GetForEntryAsync(
        Guid clientId, Guid entryId, CancellationToken cancellationToken = default) =>
        await _audit.Find(a => a.ClientId == clientId && a.EntryId == entryId)
            .SortBy(a => a.Sequence)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AuditRecordDocument>> GetForClientAsync(
        Guid clientId, CancellationToken cancellationToken = default) =>
        await _audit.Find(a => a.ClientId == clientId)
            .SortBy(a => a.Sequence)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Verify the client's chain: every record must link to its predecessor and its stored
    /// hash must recompute. Any after-the-fact edit (even by a DBA) breaks this.
    /// </summary>
    public async Task<bool> VerifyAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        List<AuditRecordDocument> records = await _audit
            .Find(a => a.ClientId == clientId)
            .SortBy(a => a.Sequence)
            .ToListAsync(cancellationToken);

        var previousHash = string.Empty;
        foreach (AuditRecordDocument record in records)
        {
            if (record.PreviousHash != previousHash || record.Hash != ComputeHash(record))
                return false;

            previousHash = record.Hash;
        }

        return true;
    }

    private static ActorSnapshot ToSnapshot(Actor actor) => new()
    {
        UserId = actor.UserId,
        Name = actor.Name,
        Claims = actor.Claims.Select(c => new ClaimDocument { Type = c.Type, Value = c.Value }).ToList(),
    };

    private static string ComputeHash(AuditRecordDocument r)
    {
        long atMs = new DateTimeOffset(DateTime.SpecifyKind(r.At, DateTimeKind.Utc), TimeSpan.Zero).ToUnixTimeMilliseconds();

        string claims = string.Join(",", r.Actor.Claims
            .OrderBy(c => c.Type, StringComparer.Ordinal)
            .ThenBy(c => c.Value, StringComparer.Ordinal)
            .Select(c => $"{c.Type}={c.Value}"));

        string content = string.Join("|",
            r.Sequence, r.ClientId, r.EntryId?.ToString() ?? string.Empty, r.EntryVersion, r.Action,
            atMs, r.Reason ?? string.Empty, r.Actor.UserId, r.Actor.Name ?? string.Empty, claims, r.PreviousHash);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
