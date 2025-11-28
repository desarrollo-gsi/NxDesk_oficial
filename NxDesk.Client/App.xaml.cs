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
                MessageBox.Show($"Error al iniciar servicios en segundo plano:\n{ex.Message}", "Error de Inicio", MessageBoxButton.OK, MessageBoxImage.Error);
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
            // Busca el exe en la carpeta actual (Publicación) o en la carpeta de desarrollo relativa
            string serverPath = FindExecutable("NxDesk.SignalingServer.exe", "NxDesk.SignalingServer");

            if (string.IsNullOrEmpty(serverPath))
            {
                Debug.WriteLine("Advertencia: No se encontró NxDesk.SignalingServer.exe");
                return;
            }

            _signalingProcess = StartProcessHidden(serverPath);
        }

        private void StartHostService()
        {
            string hostPath = FindExecutable("NxDesk.Host.exe", "NxDesk.Host");

            if (string.IsNullOrEmpty(hostPath))
            {
                Debug.WriteLine("Advertencia: No se encontró NxDesk.Host.exe");
                return;
            }

            _hostProcess = StartProcessHidden(hostPath);
        }

        private Process? StartProcessHidden(string filePath)
        {
            try
            {
                // Matar procesos previos si quedaron colgados (opcional, pero recomendado en desarrollo)
                string processName = Path.GetFileNameWithoutExtension(filePath);
                foreach (var p in Process.GetProcessesByName(processName))
                {
                    try { p.Kill(); } catch { }
                }

                var startInfo = new ProcessStartInfo(filePath)
                {
                    CreateNoWindow = true,        // No mostrar consola negra (Server/Host ocultos)
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(filePath),
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                return Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error iniciando {filePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Busca el ejecutable. Primero intenta en el directorio local (Modo Release/Publish),
        /// luego intenta buscar hacia atrás en la estructura de carpetas de Visual Studio (Modo Debug).
        /// </summary>
        private string FindExecutable(string fileName, string projectName)
        {
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1. Buscar en la misma carpeta (Producción)
            string localPath = Path.Combine(currentDir, fileName);
            if (File.Exists(localPath)) return localPath;

            // 2. Buscar en estructura de desarrollo (Debug)
            // Sube 4 niveles desde Client/bin/Debug/net8.0-windows hasta la raiz de la solución
            // Luego entra a ProjectName/bin/Debug/net8.0/FileName
            try
            {
                DirectoryInfo? slnDir = new DirectoryInfo(currentDir).Parent?.Parent?.Parent?.Parent;
                if (slnDir != null && slnDir.Exists)
                {
                    // Nota: El signaling server es net8.0 (sin windows), el host es net8.0-windows
                    // Buscamos en ambas posibilidades
                    string[] possibleSubPaths = {
                        Path.Combine(projectName, "bin", "Debug", "net8.0", fileName),
                        Path.Combine(projectName, "bin", "Debug", "net8.0-windows", fileName)
                    };

                    foreach (var subPath in possibleSubPaths)
                    {
                        string fullPath = Path.Combine(slnDir.FullName, subPath);
                        if (File.Exists(fullPath)) return fullPath;
                    }
                }
            }
            catch { }

            return string.Empty;
        }

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