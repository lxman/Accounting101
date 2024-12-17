using System.Windows;
using System.Windows.Controls;
using Accounting101.ViewModels.Update;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Update
{
    public partial class UpdateClientView : UserControl
    {
        public UpdateClientView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            DataContext = new UpdateClientViewModel(dataStore, taskFactory, clientId);
            InitializeComponent();
        }

        protected override void OnVisualParentChanged(DependencyObject? oldParent)
        {
            if (oldParent is not null)
            {
                DataContext = null;
            }
            base.OnVisualParentChanged(oldParent);
        }
    }
}