using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Accounting101.Data;

// Add profile data for application users by adding properties to the ApplicationUser class
public class ApplicationUser : IdentityUser
{
    [StringLength(50)]
    public string FirstName { get; set; } = string.Empty;

    [StringLength(50)]
    public string LastName { get; set; } = string.Empty;
}

