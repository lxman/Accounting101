using AspNetCore.Identity.MongoDbCore.Models;
using MongoDbGenericRepository.Attributes;

namespace Accounting101.Data;

[CollectionName("Roles")]
public class ApplicationRole : MongoIdentityRole<Guid>
{
}