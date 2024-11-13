using System.Windows;
using Accounting101.ViewModels;
using Accounting101.Views;
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
        private readonly IServiceProvider _serviceProvider;

        public App()
        {
            ServiceCollection services = new();
            services.AddSingleton<IDataStore, DataStore>();
            services.AddSingleton<MainWindow>();
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<CreateClient>();
            services.AddSingleton<CreateClientViewModel>();
            _serviceProvider = services.BuildServiceProvider();

            MainWindow? mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow?.Show();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Properties["ServiceProvider"] = _serviceProvider;
        }
    }
}