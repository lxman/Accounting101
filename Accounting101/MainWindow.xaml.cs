using System.Windows;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101
{
    public partial class MainWindow : Window
    {
        public MainWindow(IDataStore dataStore)
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel(dataStore, new JoinableTaskFactory(new JoinableTaskCollection(new JoinableTaskContext())));
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Application.Current.Shutdown();
        }
    }
}