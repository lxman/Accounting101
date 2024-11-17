using System.Windows;
using Accounting101.ViewModels;
using Accounting101.Views.List;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            ServiceCollection services = new();
            services.AddSingleton<IDataStore, DataStore>();
            services.AddSingleton<MainWindow>();
            services.AddSingleton<MainWindowViewModel>();
            services.AddTransient<ClientListView>();
            IServiceProvider serviceProvider = services.BuildServiceProvider();

            MainWindow? mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            mainWindow?.Show();
        }
    }
}