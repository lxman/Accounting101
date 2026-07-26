namespace Accounting101.Ledger.Contracts;

/// <summary>The lifecycle state of a stored document, surfaced on reads so a consumer can tell (e.g.)
/// a voided document from an issued one. Mirrors the engine's internal document state.</summary>
public enum DocumentLifecycle
{
    Draft,
    Active,
    Finalized,
    Superseded,
    Voided,
    Inactive,
}

/// <summary>
/// A document read back from the store: the module's <see cref="Body"/> plus the engine-owned facts a
/// module needs to act on it — the storage <see cref="Id"/>, the lifecycle <see cref="State"/>, and the
/// gapless <see cref="Sequence"/> assigned at finalize (null until finalized / for non-evidentiary).
/// </summary>
public sealed record DocumentResult<T>(Guid Id, DocumentLifecycle State, long? Sequence, T Body);

/// <summary>
/// A module's window onto the engine's document store, scoped to that module's namespace. The module
/// passes only the client, a logical collection it declared, a typed body, and tags — never an identity
/// (the engine derives the acting user from the authenticated request). The store routes by the
/// collection's policy and rejects operations that policy does not allow.
/// </summary>
public interface IDocumentStore
{
    // Universal
    Task<DocumentResult<T>?> GetAsync<T>(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentResult<T>>> QueryAsync<T>(Guid clientId, string collection,
        IReadOnlyDictionary<string, string> tagFilter,
        int? skip = null, int? limit = null, bool descending = true, bool includeVoided = false,
        CancellationToken cancellationToken = default);

    /// <summary>Count the documents matching the tag filter (for a paged list's Total). Honors includeVoided.</summary>
    Task<long> CountAsync(Guid clientId, string collection,
        IReadOnlyDictionary<string, string> tagFilter, bool includeVoided = false,
        CancellationToken cancellationToken = default);

    // Plain + reference (reference path is audited)
    Task PutAsync<T>(Guid clientId, string collection, Guid id, T body,
        IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default); // plain only
    Task DeactivateAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default); // reference only

    // Evidentiary
    Task<Guid> CreateAsync<T>(Guid clientId, string collection, T body,
        IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create an evidentiary document under a caller-supplied id. Returns <c>true</c> when it wrote and
    /// <c>false</c> when that id is already taken, leaving the existing document untouched — it never
    /// overwrites. Callers read <c>false</c> as "this operation is already known", which is the basis of
    /// caller-declared retry keys on money-moving writes.
    /// <para>
    /// A bool rather than an exception on the taken path: the collision is the expected outcome of a
    /// retry, and the store's exception types live in the Api assembly where module callers cannot see
    /// them.
    /// </para></summary>
    Task<bool> CreateWithIdAsync<T>(Guid clientId, string collection, Guid id, T body,
        IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default);

    Task UpdateAsync<T>(Guid clientId, string collection, Guid id, T body,
        IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default);
    Task<long> FinalizeAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default);
    Task<Guid> SupersedeAsync<T>(Guid clientId, string collection, Guid id, T newBody,
        IReadOnlyDictionary<string, string> newTags, CancellationToken cancellationToken = default);
    Task VoidAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default);

    // General counter (own {Key}_counters collection)
    Task<long> NextNumberAsync(Guid clientId, string counterName, CancellationToken cancellationToken = default);
}
