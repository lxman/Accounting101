using Accounting101.Components.Account;
using Accounting101.Data;
using Accounting101.Data.Interfaces;
using Accounting101.InMemoryState;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;

namespace Accounting101;

public class RegisterServices
{
    public WebApplication WebApplication { get; private set; }

    public RegisterServices()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddInteractiveWebAssemblyComponents();

        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<StateContainer>();
        builder.Services.AddScoped<IdentityUserAccessor>();
        builder.Services.AddScoped<IdentityRedirectManager>();
        builder.Services.AddScoped<AuthenticationStateProvider, PersistingRevalidatingAuthenticationStateProvider>();

        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            });

        string mongoConnectionString = builder.Configuration.GetConnectionString("MongoConnection") ?? throw new InvalidOperationException("Connection string 'MongoConnection' not found.");
        DataStore dataStore = new(mongoConnectionString);

        builder.Services.AddSingleton<IDataStore>(dataStore);
        builder.Services.AddSingleton<IDbManagement, DbManagement>();
        builder.Services.AddSingleton<ILoggedInUser, LoggedInUser>();

        builder.Services.AddIdentity<ApplicationUser, ApplicationRole>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddMongoDbStores<ApplicationUser, ApplicationRole, Guid>
            (
                mongoConnectionString, "Identity"
            );

        MongoClient client = new(mongoConnectionString);
        IMongoDatabase db = client.GetDatabase("Identity");
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<IMongoClient>(client);
        builder.Services.AddSingleton(new ApplicationDbContext(client, db));

        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

        WebApplication = builder.Build();
    }
}