using Accounting101.Ledger.Api.Auth;
using Accounting101.ModuleKit.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.FixedAssets.Api;

public sealed class HttpLedgerClient(
    HttpClient http, IHttpContextAccessor context,
    [FromKeyedServices("fixedassets")] ModuleCredential credential)
    : ModuleLedgerClient(http, context, credential), ILedgerClient;
