using System.Windows;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;

namespace Accounting101
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow(IDataStore dataStore)
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel(dataStore);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Application.Current.Shutdown();
        }
    }
}