using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NxDesk.Application.Interfaces;
using NxDesk.Client.ViewModels;
using NxDesk.Client.Views.WelcomeView.ViewModel;
using NxDesk.Infrastructure.Services;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace NxDesk.Client
{
    /// <summary>
    /// Esta clase asegura que los procesos hijos mueran instantáneamente
    /// si el proceso padre (esta App) se cierra, crashea o es detenido.
    /// </summary>
    public static class ChildProcessTracker
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        private static IntPtr _hJob;

        static ChildProcessTracker()
        {
            // Crea el Job Object
            _hJob = CreateJobObject(IntPtr.Zero, null);

            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = 0x2000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            };

            var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = info
            };

            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);

            try
            {
                Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
                // Configura el Job para matar procesos al cerrar el handle
                if (!SetInformationJobObject(_hJob, JobObjectInfoType.ExtendedLimitInformation, extendedInfoPtr, (uint)length))
                {
                    throw new InvalidOperationException("No se pudo configurar el Job Object para auto-kill.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(extendedInfoPtr);
            }
        }

        /// <summary>
        /// Agrega un proceso al Job Object. Windows lo matará cuando esta App termine.
        /// </summary>
        public static void AddProcess(Process process)
        {
            if (_hJob != IntPtr.Zero)
            {
                bool success = AssignProcessToJobObject(_hJob, process.Handle);
                if (!success && !process.HasExited)
                {
                    // Fallback si falla el Job Object (raro)
                    // Podrías loguear un error aquí
                }
            }
        }
    }

    // Estructuras necesarias para la API de Windows
    public enum JobObjectInfoType { ExtendedLimitInformation = 9 }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public Int64 PerProcessUserTimeLimit;
        public Int64 PerJobUserTimeLimit;
        public UInt32 LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public UInt32 ActiveProcessLimit;
        public UIntPtr Affinity;
        public UInt32 PriorityClass;
        public UInt32 SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public UInt64 ReadOperationCount;
        public UInt64 WriteOperationCount;
        public UInt64 OtherOperationCount;
        public UInt64 ReadTransferCount;
        public UInt64 WriteTransferCount;
        public UInt64 OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

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
                Debug.WriteLine($"Error iniciando servicios: {ex.Message}");
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

                var process = Process.Start(startInfo);

                if (process != null)
                {
                    ChildProcessTracker.AddProcess(process);
                }         
                return process;
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