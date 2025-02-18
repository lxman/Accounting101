using System.ComponentModel.DataAnnotations;
using AspNetCore.Identity.MongoDbCore.Models;
using MongoDbGenericRepository.Attributes;

namespace Accounting101.Data;

[CollectionName("Users")]
// Add profile data for application users by adding properties to the ApplicationUser class
public class ApplicationUser : MongoIdentityUser<Guid>
{
    [StringLength(50)]
    public string FirstName { get; set; } = string.Empty;

    [StringLength(50)]
    public string LastName { get; set; } = string.Empty;
}

