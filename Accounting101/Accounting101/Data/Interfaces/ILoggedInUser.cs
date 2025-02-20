namespace Accounting101.Data.Interfaces;

public interface ILoggedInUser
{
    bool LoggedIn { get; set; }

    ApplicationUser? User { get; set; }

    void SetUser(ApplicationUser user);

    void ClearUser();
}