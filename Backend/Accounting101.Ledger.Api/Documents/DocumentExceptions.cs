using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Documents;

/// <summary>An operation that is illegal for a collection's policy or its document's state
/// (e.g. Create on a reference collection, Update after Finalize, an undeclared collection).</summary>
public sealed class ModuleDocumentException(string message) : Exception(message);

/// <summary>The acting user / calling module was refused access to a document operation.
/// Carries the <see cref="ModuleAccessDecision"/> so the boundary can map it (all → 403).</summary>
public sealed class ModuleAccessDeniedException(string moduleKey, string collection, ModuleAccessDecision decision)
    : Exception($"Module '{moduleKey}' was denied access to '{collection}': {decision}.")
{
    public ModuleAccessDecision Decision { get; } = decision;
}
