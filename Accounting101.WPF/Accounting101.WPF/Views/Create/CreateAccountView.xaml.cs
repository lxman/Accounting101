using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.WPF.Views.Create;

public partial class CreateAccountView
{
    public CreateAccountView()
    {
        InitializeComponent();
    }

    public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client)
    {
        CheckPoint? checkPoint = null;
        if (client.CheckPointId is not null)
        {
            checkPoint = taskFactory.Run(() => dataStore.GetCheckpointAsync(client.Id));
        }
        HeaderView.SetInfo(client, checkPoint);
        AccountView.SetInfo(dataStore, taskFactory, client);
    }

    public void Save()
    {
        AccountView.Save();
    }
}