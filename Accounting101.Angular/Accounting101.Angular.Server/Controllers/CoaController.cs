using Accounting101.Angular.DataAccess.CoATemplates.ChartList;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Accounting101.Angular.Server.Models;
using Accounting101.Angular.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Angular.Server.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]
public class CoaController(
    IDataStore dataStore,
    ICoAService coaService,
    ILogger<CoaController> logger) : ControllerBase
{
    [HttpGet("available-names")]
    public ActionResult<string[]> GetAvailableChartNames()
    {
        return Ok(Enum.GetNames(typeof(AvailableCoAs)).ToList());
    }

    [HttpGet("description/{name}")]
    public ActionResult<ChartItem> GetDescription(string name)
    {
        return coaService.GetDescription(name);
    }

    [HttpPost("create")]
    public async Task<ActionResult<bool>> CreateCoAAsync([FromBody] CreateCoARequest request)
    {
        return await coaService.CreateCoAAsync(dataStore, request);
    }
}