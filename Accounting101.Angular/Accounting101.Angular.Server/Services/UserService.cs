using System.Security.Claims;
using Accounting101.Angular.Server.Controllers;
using Accounting101.Angular.Server.Identity;
using Accounting101.Angular.Server.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

#pragma warning disable CA2254

namespace Accounting101.Angular.Server.Services;

public interface IUserService
{
    Task<IActionResult> RegisterUserAsync(RegisterModel model);

    Task<IActionResult> LoginAsync(LoginModel model);

    IActionResult IsAuthenticated(ClaimsPrincipal user);

    Task<IActionResult> LogoutAsync(ClaimsPrincipal user);
}

public class UserService(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    SignInManager<ApplicationUser> signInManager,
    ILogger<AuthorizationController> logger)
    : IUserService
{
    public async Task<IActionResult> RegisterUserAsync(RegisterModel model)
    {
        IdentityResult result;
        ApplicationRole? existingRole = await roleManager.FindByNameAsync(model.Role);
        if (existingRole is null)
        {
            result = await roleManager.CreateAsync(new ApplicationRole { Name = model.Role });
            if (!result.Succeeded)
            {
                logger.LogError($"Failed to create role: {result.Errors}");
                return new BadRequestObjectResult(result.Errors);
            }
        }

        ApplicationUser user = new()
        {
            Email = model.Email,
            UserName = model.Email,
            FirstName = model.FirstName,
            LastName = model.LastName,
            PhoneNumber = model.PhoneNumber
        };

        result = await userManager.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            logger.LogError($"Failed to create user: {result.Errors}");
            return new BadRequestObjectResult(result.Errors);
        }

        result = await userManager.AddToRoleAsync(user, model.Role);
        if (result.Succeeded) return new OkObjectResult(user);
        logger.LogError($"Failed to add user to role: {result.Errors}");
        return new BadRequestObjectResult(result.Errors);
    }

    public async Task<IActionResult> LoginAsync(LoginModel model)
    {
        ApplicationUser? applicationUser = await userManager.FindByEmailAsync(model.Email);
        if (applicationUser is null)
        {
            logger.LogError($"User not found: {model.Email}");
            return new BadRequestObjectResult("User not found");
        }
        SignInResult result = await signInManager.PasswordSignInAsync(applicationUser, model.Password, false, false);
        return result.Succeeded
            ? new OkObjectResult(applicationUser)
            : new BadRequestObjectResult("Invalid login attempt");
    }

    public IActionResult IsAuthenticated(ClaimsPrincipal user)
    {
        return signInManager.IsSignedIn(user)
            ? new OkResult()
            : new UnauthorizedResult();
    }

    public async Task<IActionResult> LogoutAsync(ClaimsPrincipal user)
    {
        ApplicationUser? appUser = await userManager.GetUserAsync(user);
        if (appUser is null)
        {
            logger.LogError($"User not found: {user}");
            return new BadRequestObjectResult("User not found");
        }

        await userManager.UpdateSecurityStampAsync(appUser);
        await signInManager.SignOutAsync();
        return new OkResult();
    }
}
