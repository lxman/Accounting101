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

        AuditRecordDocument? latest = await _audit
            .Find(a => a.ClientId == clientId)
            .SortByDescending(a => a.Sequence)
            .FirstOrDefaultAsync(cancellationToken);

        long atMs = at.ToUnixTimeMilliseconds();
        AuditRecordDocument record = new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Sequence = (latest?.Sequence ?? 0) + 1,
            EntryId = entryId,
            EntryVersion = entryVersion,
            Action = action,
            Actor = ToSnapshot(actor),
            At = DateTimeOffset.FromUnixTimeMilliseconds(atMs).UtcDateTime, // ms precision — matches the hash on read-back
            Reason = reason,
            PreviousHash = latest?.Hash ?? string.Empty,
        };
        record.Hash = ComputeHash(record);

        await _audit.InsertOneAsync(record, cancellationToken: cancellationToken);
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
