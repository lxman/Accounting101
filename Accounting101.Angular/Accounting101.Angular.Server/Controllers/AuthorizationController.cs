using Accounting101.Angular.Server.Models;
using Accounting101.Angular.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

#pragma warning disable CA2254

namespace Accounting101.Angular.Server.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]
public class AuthorizationController(IUserService userService, ILogger<AuthorizationController> logger)
    : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> RegisterUserAsync([FromBody] RegisterModel model)
    {
        if (ModelState.IsValid)
        {
            return await userService.RegisterUserAsync(model);
        }
        logger.LogError($"Invalid model state: {ModelState}");
        return BadRequest(ModelState);
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> LoginAsync([FromBody] LoginModel model)
    {
        return await userService.LoginAsync(model);
    }

    [HttpPost("logout")]
    public Task<IActionResult> LogoutAsync()
    {
        return userService.LogoutAsync();
    }
}