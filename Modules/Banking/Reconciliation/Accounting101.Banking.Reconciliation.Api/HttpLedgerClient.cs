using Accounting101.Ledger.Api.Auth;
using Accounting101.ModuleKit.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>The module's POSTING ledger client. Delegates to the shared ModuleKit base: forwards the
/// caller's bearer, attaches the module credential (X-Module-Key/Secret) on writes so the engine authorizes
/// the module post/correction and stamps ViaModule="reconciliation", and relays non-success responses as a
/// typed LedgerClientException (relayed by the host's ModuleKit exception middleware, not a raw 500).</summary>
public sealed class HttpLedgerClient(
    HttpClient http, IHttpContextAccessor context,
    [FromKeyedServices("reconciliation")] ModuleCredential credential)
    : ModuleLedgerClient(http, context, credential), ILedgerClient;
