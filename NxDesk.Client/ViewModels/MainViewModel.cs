using NxDesk.Application.Interfaces;
using NxDesk.Application.DTOs;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Input; // Para ICommand
using System.Diagnostics; // <--- AGREGAR ESTO

namespace NxDesk.Client.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly IWebRTCService _webRTCService;

        // Estado de la conexión
        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); }
        }

        private string _statusText = "Esperando conexión...";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        // Imagen del video remoto
        private BitmapSource _currentFrame;
        public BitmapSource CurrentFrame
        {
            get => _currentFrame;
            set { _currentFrame = value; OnPropertyChanged(); }
        }

        // Lista de pantallas remotas
        private List<string> _remoteScreens;
        public List<string> RemoteScreens
        {
            get => _remoteScreens;
            set { _remoteScreens = value; OnPropertyChanged(); }
        }

        public MainViewModel(IWebRTCService webRTCService)
        {
            _webRTCService = webRTCService;

            _webRTCService.OnConnectionStateChanged += (state) => StatusText = state;
            _webRTCService.OnVideoFrameReceived += HandleVideoFrame;
            _webRTCService.OnScreensInfoReceived += (screens) => RemoteScreens = screens;
        }

        public async Task Connect(string hostId)
        {
            Debug.WriteLine($"[ViewModel] Iniciando conexión hacia: {hostId}");

            if (string.IsNullOrWhiteSpace(hostId))
            {
                Debug.WriteLine("[ViewModel] ID inválido, cancelando.");
                return;
            }

            StatusText = "Conectando...";

            try
            {
                Debug.WriteLine("[ViewModel] Llamando a WebRTCService.StartConnectionAsync...");

                // Si ya cambiaste la interfaz a bool, usa esto:
                // bool success = await _webRTCService.StartConnectionAsync(hostId);
                // Debug.WriteLine($"[ViewModel] Resultado de conexión: {success}");

                // Si sigues con la versión void Task:
                await _webRTCService.StartConnectionAsync(hostId);
                Debug.WriteLine("[ViewModel] WebRTCService terminó su ejecución (aparentemente exitosa).");

                IsConnected = true;
                Debug.WriteLine("[ViewModel] Estado cambiado a IsConnected = true");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewModel] ERROR al conectar: {ex}");
                StatusText = $"Error: {ex.Message}";
            }
        }

        public async Task Disconnect()
        {
            await _webRTCService.DisposeAsync();
            IsConnected = false;
            CurrentFrame = null;
            StatusText = "Desconectado.";
        }

        public void SendInput(string type, string key = null, double? x = null, double? y = null, string button = null, double? delta = null)
        {
            if (!IsConnected) return;

            var ev = new InputEvent
            {
                EventType = type,
                Key = key,
                X = x,
                Y = y,
                Button = button,
                Delta = delta
            };
            _webRTCService.SendInputEvent(ev);
        }

        public void SwitchScreen(int index)
        {
            if (!IsConnected) return;
            var ev = new InputEvent { EventType = "control", Command = "switch_screen", Value = index };
            _webRTCService.SendInputEvent(ev);
        }

        private void HandleVideoFrame(byte[] frameData)
        {
            // --- LOG PARA DEPURAR ---
            // Si ves esto en la consola, el video está llegando.
            Debug.WriteLine($"[ViewModel] Frame recibido: {frameData.Length} bytes");

            System.Windows.Application.Current.Dispatcher.Invoke(() => {
                try
                {
                    using (var stream = new MemoryStream(frameData))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = stream;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        CurrentFrame = bitmap;
                    }
                }
                catch { }
            });
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}