using Autofac;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using System.Windows;

namespace BalanceBrowser
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            ContainerBuilder? builder = new ContainerBuilder();
            builder.RegisterType<DataStore>()
                .As<IDataStore>()
                .SingleInstance();
            IContainer? container = builder.Build();

            using (ILifetimeScope? scope = container.BeginLifetimeScope())
            {
                // Create the startup window
                MainWindow wnd = new(scope.Resolve<IDataStore>());
                // Do stuff here, e.g. to the window
                wnd.Title = "Something else";
                // Show the window
                wnd.Show();
            }
        }
    }
}