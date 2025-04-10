﻿using Accounting101.Angular.DataAccess;
using Accounting101.Angular.DataAccess.CoATemplates.ChartList;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Accounting101.Angular.Server.Models;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Angular.Server.Services;

public interface ICoAService
{
    ActionResult<ChartItem?> GetDescription(string name);

    Task<ActionResult<bool>> CreateCoAAsync(IDataStore dataStore, CreateCoARequest request);
}

public class CoAService : ICoAService
{
    public ActionResult<ChartItem?> GetDescription(string name)
    {
        Charts charts = new();
        if (Enum.TryParse(name, out AvailableCoAs coa))
        {
            return new OkObjectResult(charts.ChartItems.FirstOrDefault(ci => ci.Type == coa));
        }
        return new NotFoundResult();
    }

    public async Task<ActionResult<bool>> CreateCoAAsync(IDataStore dataStore, CreateCoARequest request)
    {
        if (!Enum.TryParse(request.Name, out AvailableCoAs coa)) return new NotFoundResult();
        Client? c = await dataStore.FindClientByIdAsync(request.DbName, request.ClientId);
        if (c is null) return new NotFoundResult();
        if (await dataStore.CreateChartAsync(request.DbName, coa, c))
        {
            return new OkObjectResult(true);
        }
        return new BadRequestResult();
    }
}
