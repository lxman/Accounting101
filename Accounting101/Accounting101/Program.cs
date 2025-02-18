using Accounting101.Components;
using Accounting101.Components.Account;
using Accounting101.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;

namespace Accounting101;

public class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddInteractiveWebAssemblyComponents();

        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<IdentityUserAccessor>();
        builder.Services.AddScoped<IdentityRedirectManager>();
        builder.Services.AddScoped<AuthenticationStateProvider, PersistingRevalidatingAuthenticationStateProvider>();

        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            });

        string mongoConnectionString = builder.Configuration.GetConnectionString("MongoConnection") ?? throw new InvalidOperationException("Connection string 'MongoConnection' not found.");
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

        WebApplication app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseWebAssemblyDebugging();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        app.UseStaticFiles();
        app.UseAntiforgery();

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .AddInteractiveWebAssemblyRenderMode()
            .AddAdditionalAssemblies(typeof(Client._Imports).Assembly);

        // Add additional endpoints required by the Identity /Account Razor components.
        app.MapAdditionalIdentityEndpoints();

        app.Run();
    }
}