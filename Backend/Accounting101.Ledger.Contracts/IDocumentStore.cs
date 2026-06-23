namespace Accounting101.Ledger.Contracts;

/// <summary>
/// A module's window onto the engine's document store, scoped to that module's namespace. The module
/// passes only the client, a logical collection it declared, a typed body, and tags — never an identity
/// (the engine derives the acting user from the authenticated request). The store routes by the
/// collection's policy and rejects operations that policy does not allow.
/// </summary>
public interface IDocumentStore
{
    // Universal
    Task<T?> GetAsync<T>(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> QueryAsync<T>(Guid clientId, string collection,
        IReadOnlyDictionary<string, string> tagFilter, CancellationToken cancellationToken = default);

    // Plain + reference (reference path is audited)
    Task PutAsync<T>(Guid clientId, string collection, Guid id, T body,
        IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default); // plain only
    Task DeactivateAsync(Guid clientId, string collection, Guid id, CancellationToken cancellationToken = default); // reference only

    // Evidentiary
    Task<Guid> CreateAsync<T>(Guid clientId, string collection, T body,
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
