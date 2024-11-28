using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Create
{
    public partial class CreateClientView : UserControl
    {
        public CreateClientView(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            DataContext = new CreateClientViewModel(dataStore, taskFactory);
            InitializeComponent();
        }
    }
}
