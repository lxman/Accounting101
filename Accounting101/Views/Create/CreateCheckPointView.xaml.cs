using System.Windows.Controls;
using Accounting101.ViewModels.Create;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Create
{
    public partial class CreateCheckPointView : UserControl
    {
        public CreateCheckPointViewModel ViewModel { get; } = new();

        public CreateCheckPointView()
        {
            DataContext = ViewModel;
            InitializeComponent();
        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            ViewModel.SetInfo(dataStore, taskFactory, clientId);
        }

        public void Save()
        {
            ViewModel.Save();
        }
    }
}
