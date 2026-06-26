using Accounting101.Receivables.Api;
using Accounting101.Ledger.Api.Endpoints;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Payables.Api;
using Accounting101.Payroll.Api;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The engine. Installed modules add themselves here too — e.g. builder.Services.AddReceivables() —
// which is also where each module's identity gets stamped. "Installed" = its line is present.
builder.Services.AddLedgerEngine(builder.Configuration);
builder.Services.AddReceivables(builder.Configuration);
builder.Services.AddPayables(builder.Configuration);
builder.Services.AddPayroll(builder.Configuration);

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
    catch (BadHttpRequestException ex) when (ex.InnerException is System.Text.Json.JsonException je)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title  = "Invalid request body",
            Detail = je.Message,
        }, ctx.RequestAborted);
    }
    catch (System.Text.Json.JsonException je)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title  = "Invalid request body",
            Detail = je.Message,
        }, ctx.RequestAborted);
    }
});

app.UseAuthentication();
app.UseAuthorization();

app.MapLedgerEndpoints();
app.MapAdminEndpoints();
app.MapReceivablesEndpoints();
app.MapPayablesEndpoints();
app.MapPayrollEndpoints();

app.Run();

// Exposed so the test host (WebApplicationFactory<Program>) can target this composition root.
public partial class Program;
