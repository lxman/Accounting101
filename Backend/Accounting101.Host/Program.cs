using Accounting101.Ledger.Api.Endpoints;
using Accounting101.Ledger.Api.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The engine. Installed modules add themselves here too — e.g. builder.Services.AddInvoicing() —
// which is also where each module's identity gets stamped. "Installed" = its line is present.
builder.Services.AddLedgerEngine(builder.Configuration);

WebApplication app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapLedgerEndpoints();
app.MapAdminEndpoints();
// Modules map their endpoints here too — e.g. app.MapInvoicingEndpoints().

app.Run();

// Exposed so the test host (WebApplicationFactory<Program>) can target this composition root.
public partial class Program;
