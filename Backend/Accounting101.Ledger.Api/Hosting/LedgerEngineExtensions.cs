using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Endpoints;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Mongo.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>
/// Registers the ledger engine's services into a host's DI container — the engine is a library the host
/// composes, not a host itself. A host calls <c>AddLedgerEngine</c>, then maps the engine's endpoints
/// (<see cref="LedgerEndpoints.MapLedgerEndpoints"/> / <see cref="AdminEndpoints.MapAdminEndpoints"/>).
/// </summary>
public static class LedgerEngineExtensions
{
    public static IServiceCollection AddLedgerEngine(this IServiceCollection services, IConfiguration configuration)
    {
        // Register the ledger's GUID (binary subtype 4) and Decimal128 serializers once, before any Mongo I/O.
        LedgerMongoBootstrap.RegisterOnce();

        string connectionString = configuration["Mongo:ConnectionString"] ?? "mongodb://localhost:27017";
        string controlDatabase = configuration["Mongo:ControlDatabase"] ?? "ledger_control";

        // One Mongo client for the process; the control DB holds the client registry + memberships.
        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
        services.AddSingleton(sp =>
            new ControlStore(sp.GetRequiredService<IMongoClient>().GetDatabase(controlDatabase)));

        // Tenant resolution + per-client ledger construction (the isolation boundary).
        services.AddSingleton<IClientDatabaseResolver, ClientDatabaseResolver>();
        services.AddSingleton<ClientLedgerFactory>();
        services.AddSingleton<LedgerGateway>();

        // Document store: the engine derives the acting user from the request, and authorizes module calls.
        services.AddHttpContextAccessor();
        services.AddSingleton<ICurrentActor, HttpContextCurrentActor>();
        services.AddSingleton<ModuleAccess>();

        // Identity is IdP-agnostic: a dev-token scheme now; the claims → Actor mapping is ours and stable.
        services.AddSingleton<IActorFactory, ClaimsActorFactory>();
        services
            .AddAuthentication(DevTokenDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, DevTokenAuthenticationHandler>(DevTokenDefaults.Scheme, null);
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AdminEndpoints.Policy, policy => policy.RequireClaim("admin", "true"));
            options.AddPolicy(StepUpAuthorizationHandler.Policy, policy =>
                policy.AddRequirements(new StepUpRequirement(TimeSpan.FromMinutes(5))));
        });
        services.AddSingleton<IAuthorizationHandler, StepUpAuthorizationHandler>();

        return services;
    }
}
