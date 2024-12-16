using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Update
{
    public partial class UpdateBusinessView : UserControl
    {
        public UpdateBusinessView(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            DataContext = new UpdateBusinessViewModel(dataStore, taskFactory);
            InitializeComponent();
        }
    }
}