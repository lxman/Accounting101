namespace Accounting101.Angular.Server.IdentityApiRouteBuilder;

public class IdentityApiEndpointRouteBuilderOptions
{
    public bool ExcludeRegisterPost { get; set; }

    public bool ExcludeLoginPost { get; set; }

    public bool ExcludeRefreshPost { get; set; }

    public bool ExcludeConfirmEmailGet { get; set; }

    public bool ExcludeResendConfirmationEmailPost { get; set; }

    public bool ExcludeForgotPasswordPost { get; set; }

    public bool ExcludeResetPasswordPost { get; set; }

    public bool ExcludeManageGroup { get; set; }

    public bool Exclude2FaPost { get; set; }

    public bool ExcludeInfoGet { get; set; }

    public bool ExcludeInfoPost { get; set; }
}
