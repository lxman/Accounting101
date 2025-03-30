using Accounting101.Angular.DataAccess;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Angular.Server.Controllers;

[Authorize]
[ApiController]
[Route("person-name")]
public class PersonNameController(IDataStore dataStore, ILogger<PersonNameController> logger) : ControllerBase
{
    [HttpPost("{dbId:guid}")]
    public async Task<ActionResult<PersonName>> CreatePersonNameAsync(Guid dbId, [FromBody] PersonName personName)
    {
        if (await dataStore.CreateNameAsync(dbId.ToString(), personName) != Guid.Empty)
        {
            return Ok(personName);
        }
        return BadRequest();
    }
}
