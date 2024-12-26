using System.Windows.Controls;
using Accounting101.ViewModels.Create;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Create
{
    public partial class CreateAccountView : UserControl
    {
        public CreateAccountView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            DataContext = new CreateAccountViewModel(dataStore, taskFactory, clientId);
            InitializeComponent();
        }
    }
}