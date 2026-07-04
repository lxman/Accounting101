using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Endpoints;
using Accounting101.Ledger.Api.Platform;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Mongo.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Firm-scoped control plane: the firm for this request is resolved by FirmResolutionMiddleware into
        // FirmScope; the control stores read that firm's control DB. Scoped, one per request.
        services.AddScoped<FirmScope>();
        services.AddScoped<IFirmContext, HttpContextFirmContext>();
        services.AddScoped(sp => new ControlStore(sp.GetRequiredService<FirmScope>().RequireControlDatabase()));
        services.AddScoped(sp => new AdminAuditStore(sp.GetRequiredService<FirmScope>().RequireControlDatabase()));

        // Platform registry tier (firms + clusters). Additive: does not change client resolution yet.
        services.AddPlatformRegistry(configuration);

        // Seed the built-in capability sets (from role presets) once on startup — idempotent.
        // Order matters — the default firm must exist before capability sets seed into it.
        services.AddHostedService<DefaultFirmSeeder>();
        services.AddHostedService<CapabilitySetSeeder>();

        // Tenant resolution + per-client ledger construction (the isolation boundary).
        services.AddScoped<IClientDatabaseResolver, FirmScopedClientDatabaseResolver>();
        services.AddScoped<ClientLedgerFactory>();
        services.AddSingleton<IIndexGuard, IndexGuard>();
        services.AddScoped<LedgerGateway>();

        // Document store: the engine derives the acting user from the request, and authorizes module calls.
        services.AddHttpContextAccessor();
        services.AddSingleton<ICurrentActor, HttpContextCurrentActor>();
        services.AddScoped<ModuleAccess>();

        // Default credential authenticator: reads X-Module-Key + X-Module-Secret from the request and
        // verifies them against the control DB. Returns null when the headers are absent or the secret
        // does not match — which keeps the raw posting path working for hosts that never call AddModule.
        // AddModule also calls TryAddScoped<CredentialModuleAuthenticator> but TryAdd is idempotent, so
        // either registration order is safe.
        services.TryAddScoped<IModuleAuthenticator, CredentialModuleAuthenticator>();

        // Identity is IdP-agnostic: a dev-token scheme now; the claims → Actor mapping is ours and stable.
        services.AddSingleton<IActorFactory, ClaimsActorFactory>();
        services
            .AddAuthentication(DevTokenDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, DevTokenAuthenticationHandler>(DevTokenDefaults.Scheme, null);
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AdminEndpoints.Policy, policy => policy.RequireClaim("admin", "true"));
            options.AddPolicy(PlatformEndpoints.Policy, policy => policy.RequireClaim("platform", "true"));
            options.AddPolicy(StepUpAuthorizationHandler.Policy, policy =>
                policy.AddRequirements(new StepUpRequirement(TimeSpan.FromMinutes(5))));
        });
        services.AddSingleton<IAuthorizationHandler, StepUpAuthorizationHandler>();

        return services;
    }
}
