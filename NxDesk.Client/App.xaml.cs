using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NxDesk.Application.Interfaces;
using NxDesk.Client.ViewModels;
using NxDesk.Client.Views.WelcomeView.ViewModel;
using NxDesk.Infrastructure.Services;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace NxDesk.Client
{
    public partial class App : System.Windows.Application
    {
        public IServiceProvider Services { get; private set; }
        public IConfiguration Configuration { get; private set; }

        // Mutex para instancia única
        private static Mutex? _mutex = null;
        private const string AppName = "Global\\NxDesk.Client.UniqueKey";

        // Referencias a los procesos hijos para cerrarlos al salir
        private Process? _signalingProcess;
        private Process? _hostProcess;

        protected override void OnStartup(StartupEventArgs e)
        {
            // --- 1. LÓGICA DE INSTANCIA ÚNICA (SINGLE INSTANCE) ---
            bool createdNew;
            _mutex = new Mutex(true, AppName, out createdNew);

            if (!createdNew)
            {
                MessageBox.Show("NxDesk ya se encuentra en ejecución.", "NxDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                Current.Shutdown();
                return;
            }

            // --- 2. ORQUESTACIÓN DE PROCESOS (Arrancar Server y Host) ---
            try
            {
                StartSignalingServer();
                StartHostService();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error crítico al iniciar servicios en segundo plano:\n{ex.Message}", "Error de Inicio", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // --- 3. CONFIGURACIÓN (Appsettings y DI) ---
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            Configuration = builder.Build();

            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);

            Services = serviceCollection.BuildServiceProvider();

            // --- 4. ARRANQUE DE UI ---
            base.OnStartup(e);

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

            // ViewModels
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<WelcomeViewModel>();

            // Ventanas
            services.AddSingleton<MainWindow>();
        }

        // --- MÉTODOS DE GESTIÓN DE PROCESOS ---

        private void StartSignalingServer()
        {
            // Gracias al cambio en el .csproj, el archivo DEBE estar en la misma carpeta base
            string fileName = "NxDesk.SignalingServer.exe";
            string serverPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            if (!File.Exists(serverPath))
            {
                // AVISO IMPORTANTE: Si ves este error, haz "Rebuild Solution" para que el script del csproj copie los archivos.
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
                // Matar procesos previos si quedaron colgados (opcional, útil en desarrollo)
                string processName = Path.GetFileNameWithoutExtension(filePath);
                foreach (var p in Process.GetProcessesByName(processName))
                {
                    try { p.Kill(); } catch { }
                }

                var startInfo = new ProcessStartInfo(filePath)
                {
                    CreateNoWindow = true,        // No mostrar consola negra
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

        // Eliminamos el método complejo FindExecutable, ya no es necesario.

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // Cerrar procesos hijos al salir del Cliente
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

                // Liberar Mutex
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