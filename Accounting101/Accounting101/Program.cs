using Accounting101.Components;
using Accounting101.Components.Account;
using Accounting101.Data;
using Accounting101.Data.Interfaces;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.VisualStudio.Threading;
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

        WebApplication app = builder.Build();

        // For async
        JoinableTaskFactory jtf = new(new JoinableTaskCollection(new JoinableTaskContext()));

        // Make sure the initial roles of User and Administrator are created.
        IServiceProvider services = app.Services;
        using IServiceScope scope = services.CreateScope();

        // Create the initial roles if they don't exist.
        RoleManager<ApplicationRole> roleManager =
            scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!jtf.Run(() => roleManager.RoleExistsAsync("User")))
        {
            jtf.Run(() => roleManager.CreateAsync(new ApplicationRole { Name = "User" }));
        }

        if (!jtf.Run(() => roleManager.RoleExistsAsync("Administrator")))
        {
            jtf.Run(() => roleManager.CreateAsync(new ApplicationRole { Name = "Administrator" }));
        }

        // Create the initial Administrator user if it doesn't exist.
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser? me = jtf.Run(() => userManager.FindByNameAsync("jordan.mymail@gmail.com"));
        if (me is not null && !jtf.Run(() => userManager.IsInRoleAsync(me, "Administrator")))
        {
            jtf.Run(() => userManager.AddToRolesAsync(me, new List<string> { "Administrator", "User" }));
        }

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