using System.Windows;
using System.Windows.Controls;
using Accounting101.ViewModels.Update;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Update
{
    public partial class UpdateBusinessView : UserControl
    {
        public UpdateBusinessView(IDataStore dataStore, JoinableTaskFactory taskFactory, bool clientsExist)
        {
            DataContext = new UpdateBusinessViewModel(dataStore, taskFactory, clientsExist);
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