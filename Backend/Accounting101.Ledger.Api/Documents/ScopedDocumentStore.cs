using System.Collections.Concurrent;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;
using Accounting101.Ledger.Mongo.Documents;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Documents;

/// <summary>
/// One module's view of the document store, bound to its <see cref="ModuleIdentity"/>. Per call it
/// derives the acting user, authorizes via <see cref="ModuleAccess"/>, imposes the module's namespace
/// prefix, and delegates to the per-client <see cref="MongoDocumentStore"/>. Reference/evidentiary
/// mutations are audited on the client's chain (added in later slices).
/// </summary>
public sealed class ScopedDocumentStore(
    ModuleIdentity identity,
    ModuleManifest manifest,
    IClientDatabaseResolver resolver,
    ICurrentActor currentActor,
    ModuleAccess access) : IDocumentStore
{
    private readonly ConcurrentDictionary<string, bool> _indexed = new();

    // ---- plain policy ----

    public async Task PutAsync<T>(Guid clientId, string collection, Guid id, T body,
        IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default)
    {
        CollectionPolicy policy = manifest.PolicyOf(collection);
        if (policy == CollectionPolicy.Evidentiary)
            throw new ModuleDocumentException($"Put is not valid on the evidentiary collection '{collection}'; use Create/Update.");

        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);
        if (policy == CollectionPolicy.Reference)
        {
            await PutReferenceAsync(clientId, ctx, id, body, tags, cancellationToken);
            return;
        }

        ModuleDocument? existing = await ctx.Store.GetAsync(ctx.Physical, id, null, cancellationToken);
        ModuleDocument doc = BuildDoc(id, body, tags, DocumentState.Active, (existing?.Version ?? 0) + 1, existing);
        await ctx.Store.PutAsync(ctx.Physical, doc, null, cancellationToken);
    }

    public async Task<T?> GetAsync<T>(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default)
    {
        manifest.PolicyOf(collection);
        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);
        ModuleDocument? doc = await ctx.Store.GetAsync(ctx.Physical, id, null, cancellationToken);
        return doc is null ? default : BsonSerializer.Deserialize<T>(doc.Body);
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(Guid clientId, string collection,
        IReadOnlyDictionary<string, string> tagFilter, CancellationToken cancellationToken = default)
    {
        manifest.PolicyOf(collection);
        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);
        IReadOnlyList<ModuleDocument> docs = await ctx.Store.QueryAsync(ctx.Physical, tagFilter, cancellationToken);
        return docs.Select(d => BsonSerializer.Deserialize<T>(d.Body)).ToList();
    }

    public async Task DeleteAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default)
    {
        if (manifest.PolicyOf(collection) != CollectionPolicy.Plain)
            throw new ModuleDocumentException($"Delete is only valid on plain collections; '{collection}' is not plain.");
        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);
        await ctx.Store.DeleteAsync(ctx.Physical, id, null, cancellationToken);
    }

    // ---- reference policy (Task 7) ----
    public async Task DeactivateAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default)
    {
        if (manifest.PolicyOf(collection) != CollectionPolicy.Reference)
            throw new ModuleDocumentException($"Deactivate is only valid on reference collections; '{collection}' is not reference.");
        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);

        ModuleDocument existing = await ctx.Store.GetAsync(ctx.Physical, id, null, cancellationToken)
            ?? throw new ModuleDocumentException($"No document '{id}' in '{collection}' to deactivate.");

        existing.State = DocumentState.Inactive;
        existing.Version += 1;
        await AuditedPutAsync(clientId, ctx, existing, id, AuditAction.DocumentDeactivated,
            $"Deactivated {ctx.Physical}/{id}", cancellationToken);
    }

    // ---- evidentiary policy (Task 8) ----

    public async Task<Guid> CreateAsync<T>(Guid clientId, string collection, T body,
        IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default)
    {
        RequireEvidentiary(collection);
        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);
        Guid id = Guid.NewGuid();
        ModuleDocument doc = BuildDoc(id, body, tags, DocumentState.Draft, 1, null);
        await ctx.Store.PutAsync(ctx.Physical, doc, null, cancellationToken); // draft: no audit
        return id;
    }

    public async Task UpdateAsync<T>(Guid clientId, string collection, Guid id, T body,
        IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default)
    {
        RequireEvidentiary(collection);
        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);
        ModuleDocument current = await Require(ctx, collection, id, cancellationToken);
        if (current.State != DocumentState.Draft)
            throw new ModuleDocumentException($"Document '{id}' in '{collection}' is {current.State}; only a Draft may be updated.");

        ModuleDocument doc = BuildDoc(id, body, tags, DocumentState.Draft, current.Version + 1, current);
        await ctx.Store.PutAsync(ctx.Physical, doc, null, cancellationToken); // draft: no audit
    }

    public async Task<long> FinalizeAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default)
    {
        RequireEvidentiary(collection);
        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);
        ModuleDocument current = await Require(ctx, collection, id, cancellationToken);
        if (current.State != DocumentState.Draft)
            throw new ModuleDocumentException($"Document '{id}' in '{collection}' is {current.State}; only a Draft may be finalized.");

        MongoSequenceStore counters = new(ctx.Db, identity.Prefix + "counters");
        MongoAuditLog audit = new(ctx.Db);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long assigned = 0;
        int nextVersion = current.Version + 1;

        using IClientSessionHandle session = await ctx.Db.Client.StartSessionAsync(cancellationToken: cancellationToken);
        await session.WithTransactionAsync(
            async (s, _) =>
            {
                assigned = await counters.NextAsync(collection, s, cancellationToken);
                current.State = DocumentState.Finalized;
                current.Sequence = assigned;
                current.Version = nextVersion;
                await ctx.Store.PutAsync(ctx.Physical, current, s, cancellationToken);
                await audit.AppendAsync(clientId, id, current.Version, AuditAction.DocumentFinalized, ctx.Actor,
                    $"Finalized {ctx.Physical}/{id} as #{assigned}", now, s, cancellationToken);
                return true;
            },
            cancellationToken: cancellationToken);

        return assigned;
    }

    public async Task<Guid> SupersedeAsync<T>(Guid clientId, string collection, Guid id, T newBody,
        IReadOnlyDictionary<string, string> newTags, CancellationToken cancellationToken = default)
    {
        RequireEvidentiary(collection);
        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);
        ModuleDocument current = await Require(ctx, collection, id, cancellationToken);
        if (current.State != DocumentState.Finalized)
            throw new ModuleDocumentException($"Document '{id}' in '{collection}' is {current.State}; only a Finalized document may be superseded.");

        Guid newId = Guid.NewGuid();
        MongoSequenceStore counters = new(ctx.Db, identity.Prefix + "counters");
        MongoAuditLog audit = new(ctx.Db);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int nextVersion = current.Version + 1;

        using IClientSessionHandle session = await ctx.Db.Client.StartSessionAsync(cancellationToken: cancellationToken);
        await session.WithTransactionAsync(
            async (s, _) =>
            {
                long assigned = await counters.NextAsync(collection, s, cancellationToken);

                ModuleDocument replacement = BuildDoc(newId, newBody, newTags, DocumentState.Finalized, 1, null);
                replacement.Sequence = assigned;
                replacement.Supersedes = id;

                current.State = DocumentState.Superseded;
                current.SupersededBy = newId;
                current.Version = nextVersion;

                await ctx.Store.PutAsync(ctx.Physical, replacement, s, cancellationToken);
                await ctx.Store.PutAsync(ctx.Physical, current, s, cancellationToken);
                await audit.AppendAsync(clientId, id, current.Version, AuditAction.DocumentSuperseded, ctx.Actor,
                    $"Superseded {ctx.Physical}/{id} by {newId} (#{assigned})", now, s, cancellationToken);
                return true;
            },
            cancellationToken: cancellationToken);

        return newId;
    }

    public async Task VoidAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default)
    {
        RequireEvidentiary(collection);
        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);
        ModuleDocument current = await Require(ctx, collection, id, cancellationToken);
        if (current.State != DocumentState.Finalized)
            throw new ModuleDocumentException($"Document '{id}' in '{collection}' is {current.State}; only a Finalized document may be voided.");

        current.State = DocumentState.Voided;
        current.Version += 1;
        await AuditedPutAsync(clientId, ctx, current, id, AuditAction.DocumentVoided, $"Voided {ctx.Physical}/{id}", cancellationToken);
    }

    public async Task<long> NextNumberAsync(Guid clientId, string counterName, CancellationToken cancellationToken = default)
    {
        Actor actor = currentActor.Get();
        ModuleAccessDecision decision = await access.AuthorizeAsync(identity, identity.Key, actor.UserId, clientId, cancellationToken);
        if (decision != ModuleAccessDecision.Allowed)
            throw new ModuleAccessDeniedException(identity.Key, counterName, decision);
        IMongoDatabase db = await resolver.ResolveAsync(clientId, cancellationToken)
            ?? throw new ModuleAccessDeniedException(identity.Key, counterName, ModuleAccessDecision.NotMember);

        MongoSequenceStore counters = new(db, identity.Prefix + "counters");
        return await counters.NextAsync(counterName, null, cancellationToken);
    }

    // ---- shared ----

    private void RequireEvidentiary(string collection)
    {
        if (manifest.PolicyOf(collection) != CollectionPolicy.Evidentiary)
            throw new ModuleDocumentException($"This operation is only valid on evidentiary collections; '{collection}' is not evidentiary.");
    }

    private static async Task<ModuleDocument> Require(Ctx ctx, string collection, Guid id, CancellationToken cancellationToken) =>
        await ctx.Store.GetAsync(ctx.Physical, id, null, cancellationToken)
        ?? throw new ModuleDocumentException($"No document '{id}' in '{collection}'.");

    private async Task PutReferenceAsync<T>(Guid clientId, Ctx ctx, Guid id, T body,
        IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
    {
        ModuleDocument? prior = await ctx.Store.GetAsync(ctx.Physical, id, null, cancellationToken);
        AuditAction action = prior is null ? AuditAction.DocumentCreated : AuditAction.DocumentUpdated;
        string summary = prior is null
            ? $"Created {ctx.Physical}/{id}"
            : $"Updated {ctx.Physical}/{id}{TagDiff(prior.Tags, tags)} (body updated)";

        ModuleDocument doc = BuildDoc(id, body, tags, DocumentState.Active, (prior?.Version ?? 0) + 1, prior);
        await AuditedPutAsync(clientId, ctx, doc, id, action, summary, cancellationToken);
    }

    /// <summary>Replace the document and append its audit record in one transaction — a change can never land unaudited.</summary>
    private async Task AuditedPutAsync(Guid clientId, Ctx ctx, ModuleDocument doc, Guid id,
        AuditAction action, string summary, CancellationToken cancellationToken)
    {
        MongoAuditLog audit = new(ctx.Db);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using IClientSessionHandle session = await ctx.Db.Client.StartSessionAsync(cancellationToken: cancellationToken);
        await session.WithTransactionAsync(
            async (s, _) =>
            {
                await ctx.Store.PutAsync(ctx.Physical, doc, s, cancellationToken);
                await audit.AppendAsync(clientId, id, doc.Version, action, ctx.Actor, summary, now, s, cancellationToken);
                return true;
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>A compact "tag: old → new" of the engine-visible tags (the opaque body is not diffable).</summary>
    private static string TagDiff(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        List<string> changes = [];
        foreach (string key in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(key, out string? b);
            after.TryGetValue(key, out string? a);
            if (b != a)
                changes.Add($"{key}: {b ?? "null"} → {a ?? "null"}");
        }
        return changes.Count == 0 ? "" : " [" + string.Join("; ", changes) + "]";
    }

    private readonly record struct Ctx(IMongoDatabase Db, Actor Actor, MongoDocumentStore Store, string Physical);

    private async Task<Ctx> EnterAsync(Guid clientId, string collection, CancellationToken cancellationToken)
    {
        Actor actor = currentActor.Get();
        ModuleAccessDecision decision = await access.AuthorizeAsync(identity, identity.Key, actor.UserId, clientId, cancellationToken);
        if (decision != ModuleAccessDecision.Allowed)
            throw new ModuleAccessDeniedException(identity.Key, collection, decision);

        IMongoDatabase db = await resolver.ResolveAsync(clientId, cancellationToken)
            ?? throw new ModuleAccessDeniedException(identity.Key, collection, ModuleAccessDecision.NotMember);

        string physical = identity.Prefix + collection;
        MongoDocumentStore store = new(db);
        await EnsureIndexesAsync(clientId, collection, physical, store, cancellationToken);
        return new Ctx(db, actor, store, physical);
    }

    private async Task EnsureIndexesAsync(Guid clientId, string collection, string physical, MongoDocumentStore store, CancellationToken cancellationToken)
    {
        string latch = clientId + "/" + collection;
        if (!_indexed.TryAdd(latch, true))
            return;
        try
        {
            await store.EnsureTagIndexesAsync(physical, manifest.IndexedTags(collection), cancellationToken);
        }
        catch
        {
            _indexed.TryRemove(latch, out _); // re-arm so a later call retries
            throw;
        }
    }

    private static ModuleDocument BuildDoc<T>(Guid id, T body, IReadOnlyDictionary<string, string> tags,
        DocumentState state, int version, ModuleDocument? prior)
        => new()
        {
            Id = id,
            Tags = new Dictionary<string, string>(tags),
            Body = body!.ToBsonDocument(),
            State = state,
            Version = version,
            Sequence = prior?.Sequence,
            Supersedes = prior?.Supersedes,
            SupersededBy = prior?.SupersededBy,
        };
}
