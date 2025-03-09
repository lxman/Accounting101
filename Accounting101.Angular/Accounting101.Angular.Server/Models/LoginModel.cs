namespace Accounting101.Angular.Server.Models;

public class LoginModel
{
    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string MultiFactorAuthenticationCode { get; set; } = string.Empty;

    public string MultiFactorAuthenticationResetCode { get; set; } = string.Empty;
}
