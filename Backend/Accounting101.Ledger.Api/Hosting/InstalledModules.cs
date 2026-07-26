using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>
/// The module keys this deployment actually composed — the installation half of
/// "availability = installed ∩ entitled". Derived from the ModuleRegistration singletons every
/// <c>AddModule</c> call contributes, so it cannot drift from what the host built.
///
/// Taken from DI, not from the control database: ModuleRegistrar seeds a copy of each registration
/// into the DEFAULT firm's control DB only, so under multi-firm that copy can be partial or stale.
/// DI is the in-process truth, costs nothing, and needs no read.
///
/// Keys and nothing else, deliberately: ModuleRegistration carries the module's shared Secret, and
/// handing that object to a read path puts a credential one careless serialization from a response
/// body.
///
/// A dumb value object with a public constructor — discovery stays in <c>AddModule</c>, which already
/// knows the key. That is what makes the "not installed" branch testable at all: the test process
/// always has every module composed in, so tests substitute this instance instead.
/// </summary>
public sealed class InstalledModules(IEnumerable<string> keys)
{
    /// <summary>Every module key composed into this host. Ordinal comparison — keys are stable
    /// lowercase identifiers ("payables", "einvoicing"), matched the same way everywhere else.</summary>
    public IReadOnlySet<string> Keys { get; } = keys.ToHashSet(StringComparer.Ordinal);

    public bool Has(string moduleKey) => Keys.Contains(moduleKey);
}

internal static class InstalledModulesRegistration
{
    /// <summary>
    /// The one place the <see cref="InstalledModules"/> factory is defined — both <c>AddModule</c> and
    /// <c>AddLedgerEngine</c> call this instead of registering their own copy, so the factory logic
    /// (comparer, any future filter on <c>ModuleRegistration.Enabled</c>) can never drift between the
    /// two call sites.
    ///
    /// Must stay TryAdd and must stay late-bound: the factory does not run until first resolution, at
    /// request time, long after every <c>AddModule</c> call has completed, so the single resolved
    /// instance sees every module composed into the host — not just whichever call registered it first.
    /// Computing the key set eagerly here would capture only the modules registered so far.
    /// </summary>
    public static void TryAddInstalledModules(this IServiceCollection services) =>
        services.TryAddSingleton(sp => new InstalledModules(
            sp.GetServices<ModuleRegistration>().Select(m => m.Key)));
}
