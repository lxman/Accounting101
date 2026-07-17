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
    private readonly IMongoCollection<AuditHeadDocument> _head;

    static MongoAuditLog() => LedgerMongoBootstrap.RegisterOnce();

    public MongoAuditLog(IMongoDatabase database, string collectionName = "audit")
    {
        ArgumentNullException.ThrowIfNull(database);
        _audit = database.GetCollection<AuditRecordDocument>(collectionName);
        _head = database.GetCollection<AuditHeadDocument>("audit-head");
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
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        long atMs = at.ToUnixTimeMilliseconds();
        ActorSnapshot snapshot = ToSnapshot(actor);

        for (int attempt = 1; ; attempt++)
        {
            AuditRecordDocument? latest = await FindLatestAsync(clientId, session, cancellationToken);

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
                if (session is null)
                    await _audit.InsertOneAsync(record, cancellationToken: cancellationToken);
                else
                    await _audit.InsertOneAsync(session, record, cancellationToken: cancellationToken);

                await AdvanceHeadAsync(clientId, record.Sequence, record.Hash, session, cancellationToken);
                return;
            }
            catch (MongoWriteException ex)
                when (session is null && ex.WriteError?.Category == ServerErrorCategory.DuplicateKey && attempt < MaxAppendAttempts)
            {
                // Standalone: a concurrent append took this sequence — re-read the tail and re-chain.
                // Inside a transaction the conflict propagates instead, so the transaction re-runs the whole op.
            }
        }
    }

    /// <summary>
    /// Monotonically advances the per-client chain-head to (<paramref name="sequence"/>, <paramref name="hash"/>).
    /// The guarded upsert only matches when the stored sequence is strictly less than the new value,
    /// so the head never regresses. When no match is found and an upsert insert collides on the unique
    /// <c>_id = ClientId</c> key (meaning a concurrent writer already advanced the head to ≥ sequence),
    /// the DuplicateKey is caught and treated as a safe no-op.
    /// </summary>
    internal async Task AdvanceHeadAsync(
        Guid clientId,
        long sequence,
        string hash,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default)
    {
        FilterDefinition<AuditHeadDocument> filter = Builders<AuditHeadDocument>.Filter.And(
            Builders<AuditHeadDocument>.Filter.Eq(h => h.ClientId, clientId),
            Builders<AuditHeadDocument>.Filter.Lt(h => h.Sequence, sequence));

        UpdateDefinition<AuditHeadDocument> update = Builders<AuditHeadDocument>.Update
            .SetOnInsert(h => h.ClientId, clientId)
            .Set(h => h.Sequence, sequence)
            .Set(h => h.Hash, hash);

        UpdateOptions opts = new() { IsUpsert = true };

        try
        {
            if (session is null)
                await _head.UpdateOneAsync(filter, update, opts, cancellationToken);
            else
                await _head.UpdateOneAsync(session, filter, update, opts, cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The head is already at or beyond `sequence` — the monotonic invariant holds; no-op.
            // In a transaction the single-advance-per-append invariant means this insert-collision branch
            // is not reached on the normal path. If a future change advances the head twice per transaction,
            // a DuplicateKey here would abort the transaction (not a silent no-op).
        }
    }

    /// <summary>
    /// Returns the current chain-head for <paramref name="clientId"/>, or <c>null</c> if no records
    /// have been appended yet. Task 2 uses this in VerifyAsync to detect tail truncation.
    /// </summary>
    internal async Task<AuditHeadDocument?> FindHeadAsync(
        Guid clientId,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default)
    {
        IFindFluent<AuditHeadDocument, AuditHeadDocument> find = session is null
            ? _head.Find(h => h.ClientId == clientId)
            : _head.Find(session, h => h.ClientId == clientId);
        return await find.FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<AuditRecordDocument?> FindLatestAsync(Guid clientId, IClientSessionHandle? session, CancellationToken cancellationToken)
    {
        IFindFluent<AuditRecordDocument, AuditRecordDocument> find = session is null
            ? _audit.Find(a => a.ClientId == clientId)
            : _audit.Find(session, a => a.ClientId == clientId);
        return await find.SortByDescending(a => a.Sequence).FirstOrDefaultAsync(cancellationToken);
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

    /// <summary>
    /// The client's audit records in sequence order. <paramref name="skip"/>/<paramref name="limit"/> page
    /// the result (limit &lt;= 0 means no limit); the endpoint applies a default cap so an unbounded scan
    /// can't be requested by accident.
    /// </summary>
    public async Task<IReadOnlyList<AuditRecordDocument>> GetForClientAsync(
        Guid clientId, int skip = 0, int limit = 0, CancellationToken cancellationToken = default) =>
        await _audit.Find(a => a.ClientId == clientId)
            .SortBy(a => a.Sequence)
            .Skip(skip > 0 ? skip : null)
            .Limit(limit > 0 ? limit : null)
            .ToListAsync(cancellationToken);

    /// <summary>The count of the client's audit records — the <c>Total</c> for a paged audit-log response.</summary>
    public Task<long> CountForClientAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        _audit.CountDocumentsAsync(a => a.ClientId == clientId, cancellationToken: cancellationToken);

    /// <summary>
    /// Verify the client's chain and, when broken, diagnose how: every record must link to its
    /// predecessor and its stored hash must recompute, and the walked tail must reconcile with the
    /// guarded head (which catches tail truncation). The failure taxonomy maps 1:1 to the checks below.
    /// </summary>
    public async Task<AuditChainVerification> VerifyDetailedAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        List<AuditRecordDocument> records = await _audit
            .Find(a => a.ClientId == clientId)
            .SortBy(a => a.Sequence)
            .ToListAsync(cancellationToken);

        AuditHeadDocument? head = await FindHeadAsync(clientId, cancellationToken: cancellationToken);
        long? headSeq = head?.Sequence;

        var previousHash = string.Empty;
        long expectedSeq = 1;
        foreach (AuditRecordDocument record in records)
        {
            if (record.Sequence != expectedSeq)
                return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.SequenceGap, expectedSeq);
            if (record.PreviousHash != previousHash)
                return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.BrokenLink, record.Sequence);
            if (record.Hash != ComputeHash(record))
                return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.HashMismatch, record.Sequence);

            previousHash = record.Hash;
            expectedSeq++;
        }

        if (records.Count == 0)
            return head is null || head.Sequence == 0
                ? new AuditChainVerification(true, 0, headSeq, null, null)
                : new AuditChainVerification(false, 0, headSeq, AuditChainFailure.TailTruncated, 1);

        AuditRecordDocument last = records[^1];
        if (head is null || head.Sequence < last.Sequence)
            return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.HeadMismatch, null);
        if (head.Sequence > last.Sequence)
            return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.TailTruncated, last.Sequence + 1);
        if (head.Hash != last.Hash)
            return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.HeadMismatch, null);

        return new AuditChainVerification(true, records.Count, headSeq, null, null);
    }

    /// <summary>Pass/fail chain verification — delegates to <see cref="VerifyDetailedAsync"/>. Behavior
    /// preserved for existing callers.</summary>
    public async Task<bool> VerifyAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        (await VerifyDetailedAsync(clientId, cancellationToken)).Valid;

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
