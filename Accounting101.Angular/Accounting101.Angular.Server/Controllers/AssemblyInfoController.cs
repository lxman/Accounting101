using System.Globalization;
using System.Reflection;
using Accounting101.Angular.Server.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Angular.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class AssemblyInfoController : ControllerBase
{
    [HttpGet("build-time")]
    public string GetBuildTime() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<BuildTimeAttribute>()?.BuildTime.ToString(CultureInfo.InvariantCulture) ?? "N/A";
}
