using Accounting101.Ledger.Api.Auth;
using Accounting101.ModuleKit.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Receivables.Api;

/// <summary>Receivables' ledger client — a thin subclass of the shared <see cref="ModuleLedgerClient"/>
/// base that supplies the keyed receivables module credential and implements <see cref="ILedgerClient"/>
/// (its public base members satisfy the interface, including <c>ApproveAsync</c>/<c>ValidateAsync</c>/
/// <c>GetSubledgerAsync</c>).</summary>
public sealed class HttpLedgerClient(
    HttpClient http, IHttpContextAccessor context,
    [FromKeyedServices("receivables")] ModuleCredential credential)
    : ModuleLedgerClient(http, context, credential), ILedgerClient;
