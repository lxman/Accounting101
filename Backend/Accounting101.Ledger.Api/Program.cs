using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Endpoints;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Mongo.Serialization;
using Microsoft.AspNetCore.Authentication;
using MongoDB.Driver;

// Register the ledger's GUID (binary subtype 4) and Decimal128 serializers once, before any Mongo I/O.
LedgerMongoBootstrap.RegisterOnce();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration["Mongo:ConnectionString"] ?? "mongodb://localhost:27017";
string controlDatabase = builder.Configuration["Mongo:ControlDatabase"] ?? "ledger_control";

// One Mongo client for the process; the control DB holds the client registry + memberships.
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
builder.Services.AddSingleton(sp =>
    new ControlStore(sp.GetRequiredService<IMongoClient>().GetDatabase(controlDatabase)));

// Tenant resolution + per-client ledger construction (the isolation boundary).
builder.Services.AddSingleton<IClientDatabaseResolver, ClientDatabaseResolver>();
builder.Services.AddSingleton<ClientLedgerFactory>();
builder.Services.AddSingleton<LedgerGateway>();

// Identity is IdP-agnostic: a dev-token scheme now; the claims → Actor mapping is ours and stable.
builder.Services.AddSingleton<IActorFactory, ClaimsActorFactory>();
builder.Services
    .AddAuthentication(DevTokenDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, DevTokenAuthenticationHandler>(DevTokenDefaults.Scheme, null);
builder.Services.AddAuthorization(options =>
    options.AddPolicy(AdminEndpoints.Policy, policy => policy.RequireClaim("admin", "true")));

WebApplication app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapLedgerEndpoints();
app.MapAdminEndpoints();

app.Run();
