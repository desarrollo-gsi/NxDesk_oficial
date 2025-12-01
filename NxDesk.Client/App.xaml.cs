using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NxDesk.Application.Interfaces;
using NxDesk.Client.ViewModels;
using NxDesk.Client.Views.WelcomeView.ViewModel;
using NxDesk.Infrastructure.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace NxDesk.Client
{
    public partial class App : System.Windows.Application
    {
        public IServiceProvider Services { get; private set; }
        public IConfiguration Configuration { get; private set; }

        private static Mutex? _mutex = null;
        private const string AppName = "Global\\NxDesk.Client.UniqueKey";

        private Process? _signalingProcess;
        private Process? _hostProcess;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _mutex = new Mutex(true, AppName, out createdNew);

            if (!createdNew)
            {
                MessageBox.Show("NxDesk ya se encuentra en ejecución.", "NxDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                Current.Shutdown();
                return;
            }

            try
            {
                StartSignalingServer();
                StartHostService();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error crítico al iniciar servicios en segundo plano:\n{ex.Message}", "Error de Inicio", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            Configuration = builder.Build();

            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);

            Services = serviceCollection.BuildServiceProvider();

            base.OnStartup(e);

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(Configuration);
            services.AddSingleton<ISignalingService, SignalRSignalingService>();
            services.AddSingleton<IWebRTCService, SIPSorceryWebRTCService>();
            services.AddSingleton<IIdentityService, IdentityService>();
            services.AddSingleton<INetworkDiscoveryService, NetworkDiscoveryService>();

            services.AddSingleton<MainViewModel>();
            services.AddSingleton<WelcomeViewModel>();

            services.AddSingleton<MainWindow>();
        }

        private void StartSignalingServer()
        {
            string fileName = "NxDesk.SignalingServer.exe";
            string serverPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            if (!File.Exists(serverPath))
            {
                MessageBox.Show($"No se encontró el archivo: {fileName}\nUbicación buscada: {serverPath}\n\nIntenta recompilar la solución.",
                                "Falta Componente", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _signalingProcess = StartProcessHidden(serverPath);
        }

        private void StartHostService()
        {
            string fileName = "NxDesk.Host.exe";
            string hostPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            if (!File.Exists(hostPath))
            {
                MessageBox.Show($"No se encontró el archivo: {fileName}\nUbicación buscada: {hostPath}\n\nIntenta recompilar la solución.",
                                "Falta Componente", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _hostProcess = StartProcessHidden(hostPath);
        }

        private Process? StartProcessHidden(string filePath)
        {
            try
            {
                string processName = Path.GetFileNameWithoutExtension(filePath);
                foreach (var p in Process.GetProcessesByName(processName))
                {
                    try { p.Kill(); } catch { }
                }

                var startInfo = new ProcessStartInfo(filePath)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(filePath),
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                return Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error iniciando proceso {Path.GetFileName(filePath)}: {ex.Message}");
                return null;
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (_signalingProcess != null && !_signalingProcess.HasExited)
                {
                    _signalingProcess.Kill();
                    _signalingProcess.Dispose();
                }

                if (_hostProcess != null && !_hostProcess.HasExited)
                {
                    _hostProcess.Kill();
                    _hostProcess.Dispose();
                }

                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error al cerrar App: {ex.Message}");
            }
            finally
            {
                base.OnExit(e);
            }
        }
    }
}