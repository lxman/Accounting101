using System.Windows.Controls;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Create
{
    public partial class CreateAccountView : UserControl
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
}