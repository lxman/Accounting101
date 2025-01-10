using System.Windows.Controls;
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
            HeaderView.SetInfo(client);
            AccountView.SetInfo(dataStore, taskFactory, client);
        }

        public void Save()
        {
            AccountView.Save();
        }
    }
}