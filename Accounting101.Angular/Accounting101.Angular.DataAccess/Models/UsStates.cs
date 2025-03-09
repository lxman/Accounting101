using System.Collections.Generic;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable VSTHRD002

namespace Accounting101.Angular.DataAccess.Models;

public class UsStates
{
    public List<string> States { get; }

    public UsStates(IDataStore dataStore, JoinableTaskFactory taskFactory)
    {
        States = taskFactory.Run(dataStore.GetStatesAsync);
    }
}