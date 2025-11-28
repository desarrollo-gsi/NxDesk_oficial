using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Windows; 
using NxDesk.Client.Views.WelcomeView.ViewModel;
using NxDesk.Application.Interfaces;
using NxDesk.Infrastructure.Services;
using NxDesk.Client.ViewModels;

namespace NxDesk.Client
{
    public partial class App : System.Windows.Application
    {
        public IServiceProvider Services { get; private set; }
        public IConfiguration Configuration { get; private set; }

        private void OnStartup(object sender, StartupEventArgs e)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            Configuration = builder.Build();

            // Configurar servicios (DI)
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);

            Services = serviceCollection.BuildServiceProvider();

            // Iniciar la ventana principal
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(Configuration);

            // Infraestructura
            services.AddSingleton<ISignalingService, SignalRSignalingService>();
            services.AddSingleton<IWebRTCService, SIPSorceryWebRTCService>();
            services.AddSingleton<IIdentityService, IdentityService>();
            services.AddSingleton<INetworkDiscoveryService, NetworkDiscoveryService>();

            // ViewModels y Ventanas
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<WelcomeViewModel>();
            services.AddSingleton<MainWindow>();
        }
    }
}