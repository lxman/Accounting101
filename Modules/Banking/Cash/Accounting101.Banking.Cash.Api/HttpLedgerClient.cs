using Accounting101.Ledger.Api.Auth;
using Accounting101.ModuleKit.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Banking.Cash.Api;

/// <summary>Cash's ledger client — a thin subclass of the shared <see cref="ModuleLedgerClient"/> base
/// that supplies the keyed cash module credential and implements <see cref="ILedgerClient"/> (its public
/// base members satisfy the interface).</summary>
public sealed class HttpLedgerClient(
    HttpClient http, IHttpContextAccessor context,
    [FromKeyedServices("cash")] ModuleCredential credential)
    : ModuleLedgerClient(http, context, credential), ILedgerClient;
