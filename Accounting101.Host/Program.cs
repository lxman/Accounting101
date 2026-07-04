using Accounting101.Receivables.Api;
using Accounting101.Ledger.Api.Endpoints;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Payables.Api;
using Accounting101.Payroll.Api;
using Accounting101.Banking.Cash.Api;
using Accounting101.Banking.Reconciliation.Api;
using Accounting101.Ledger.Api.Platform;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The engine. Installed modules add themselves here too — e.g. builder.Services.AddReceivables() —
// which is also where each module's identity gets stamped. "Installed" = its line is present.
builder.Services.AddLedgerEngine(builder.Configuration);
builder.Services.AddReceivables(builder.Configuration);
builder.Services.AddPayables(builder.Configuration);
builder.Services.AddPayroll(builder.Configuration);
builder.Services.AddCash(builder.Configuration);
builder.Services.AddReconciliation(builder.Configuration);

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
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title  = "Forbidden",
            Detail = ex.Message,
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
app.MapCapabilityCatalogEndpoints();
app.MapCapabilitySetEndpoints();
app.MapAdminAuditEndpoints();
app.MapPlatformEndpoints();
app.MapReceivablesEndpoints();
app.MapPayablesEndpoints();
app.MapPayrollEndpoints();
app.MapCashEndpoints();
app.MapReconciliationEndpoints();

app.Run();

// Exposed so the test host (WebApplicationFactory<Program>) can target this composition root.
public partial class Program;
