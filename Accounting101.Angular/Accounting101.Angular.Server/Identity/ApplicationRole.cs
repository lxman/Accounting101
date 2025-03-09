using AspNetCoreIdentity.MongoDriver.Models;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Angular.Server.Identity;

[BsonDiscriminator("Roles")]
[BsonIgnoreExtraElements]
public class ApplicationRole : MongoRole<Guid>
{
}