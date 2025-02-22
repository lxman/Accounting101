using Accounting101.Components;
using Accounting101.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.VisualStudio.Threading;

namespace Accounting101;

public class AppSetup
{
    public AppSetup(WebApplication app)
    {
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