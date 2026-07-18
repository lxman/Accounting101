using Accounting101.Ledger.Api.Endpoints;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Api.Platform;
using Accounting101.ModuleKit.Api;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The engine. Installed modules compose themselves via discovery: every project-referenced Api
// assembly's IModuleComposition is found and added here. "Installed" = the module's Api project
// is referenced by Host.csproj (the Modules/ list, or a Modules.Private/ overlay).
builder.Services.AddLedgerEngine(builder.Configuration);
builder.AddDiscoveredModules();

// Dev-only: let the Angular dev server (localhost:4200) call the API cross-origin.
// Not registered outside Development, so production is unaffected.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins("http://localhost:4200")
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

// Reject any JSON body that contains fields not mapped to the target DTO. This catches typos like
// "date" instead of "effectiveDate" early — before the value is silently dropped — and gives the
// caller an actionable 400 that names the offending property.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.UnmappedMemberHandling =
        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow);

WebApplication app = builder.Build();

// Surface JsonException (thrown by the strict binding above) as a structured 400 response.
// The exception message already names the offending property, so we forward it verbatim.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Accounting101.Ledger.Api.Documents.ModuleAccessDeniedException ex)
    {
        // Log the precise reason (module key + collection + ModuleAccessDecision) for operators, but keep
        // the response body generic — the internal decision name is diagnostic, not for the caller to see.
        app.Logger.LogInformation("Module access denied: {Reason}", ex.Message);
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title  = "Forbidden",
            Detail = "You are not authorized to access this module resource.",
        }, options: null, contentType: "application/problem+json", cancellationToken: ctx.RequestAborted);
    }
    catch (BadHttpRequestException ex) when (ex.InnerException is System.Text.Json.JsonException je)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title  = "Invalid request body",
            Detail = je.Message,
        }, options: null, contentType: "application/problem+json", cancellationToken: ctx.RequestAborted);
    }
    catch (System.Text.Json.JsonException je)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title  = "Invalid request body",
            Detail = je.Message,
        }, options: null, contentType: "application/problem+json", cancellationToken: ctx.RequestAborted);
    }
});

// Relay any escaping module ledger-client refusal (LedgerClientException) as problem+json carrying the
// engine's real status + reason — the single home of the module→ledger error relay.
app.UseModuleLedgerExceptionRelay();

if (app.Environment.IsDevelopment())
    app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// Resolve the request's firm (from the firm claim or the configured default) into FirmScope before any
// endpoint runs; unknown/suspended firms are refused here with 403.
app.UseFirmResolution();

app.MapLedgerEndpoints();
app.MapAdminEndpoints();
app.MapCapabilitiesEndpoints();
app.MapMemberEndpoints();
app.MapApprovalPolicyEndpoints();
app.MapPostingAccountEndpoints();
app.MapCapabilityCatalogEndpoints();
app.MapCapabilitySetEndpoints();
app.MapAdminAuditEndpoints();
app.MapPlatformEndpoints();
app.MapDiscoveredModuleEndpoints();

app.Run();

// Exposed so the test host (WebApplicationFactory<Program>) can target this composition root.
public partial class Program;
