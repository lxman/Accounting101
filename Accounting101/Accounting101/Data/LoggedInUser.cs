using Accounting101.Data.Interfaces;

namespace Accounting101.Data;

public class LoggedInUser : ILoggedInUser
{
    public bool LoggedIn { get; set; }
    public ApplicationUser? User { get; set; }

    public void SetUser(ApplicationUser user)
    {
        LoggedIn = true;
        User = user;
    }

    public void ClearUser()
    {
        LoggedIn = false;
        User = null;
    }
}
