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
            throw new ModuleDocumentException("Reference auditing is added in a later slice."); // Task 7

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
    public Task DeactivateAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default) =>
        throw new ModuleDocumentException("Deactivate is added in a later slice.");

    // ---- evidentiary policy (Task 8) ----
    public Task<Guid> CreateAsync<T>(Guid clientId, string collection, T body, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default) =>
        throw new ModuleDocumentException("Create is added in a later slice.");
    public Task UpdateAsync<T>(Guid clientId, string collection, Guid id, T body, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default) =>
        throw new ModuleDocumentException("Update is added in a later slice.");
    public Task<long> FinalizeAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default) =>
        throw new ModuleDocumentException("Finalize is added in a later slice.");
    public Task<Guid> SupersedeAsync<T>(Guid clientId, string collection, Guid id, T newBody, IReadOnlyDictionary<string, string> newTags, CancellationToken cancellationToken = default) =>
        throw new ModuleDocumentException("Supersede is added in a later slice.");
    public Task VoidAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default) =>
        throw new ModuleDocumentException("Void is added in a later slice.");
    public Task<long> NextNumberAsync(Guid clientId, string counterName, CancellationToken cancellationToken = default) =>
        throw new ModuleDocumentException("NextNumber is added in a later slice.");

    // ---- shared ----

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
