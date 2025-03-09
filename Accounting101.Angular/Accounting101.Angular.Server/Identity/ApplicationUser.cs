using AspNetCoreIdentity.MongoDriver.Models;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Angular.Server.Identity;

[BsonDiscriminator("Users")]
[BsonIgnoreExtraElements]
public class ApplicationUser : MongoUser<Guid>
{
    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;
}