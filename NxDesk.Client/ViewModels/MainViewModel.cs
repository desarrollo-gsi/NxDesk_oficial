using NxDesk.Application.Interfaces;
using NxDesk.Application.DTOs;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Media;

namespace NxDesk.Client.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly IWebRTCService _webRTCService;
        
        // WriteableBitmap reutilizable para evitar allocations
        private WriteableBitmap _writeableBitmap;
        private int _lastWidth = 0;
        private int _lastHeight = 0;
        
        // Estadísticas de rendimiento
        private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
        private int _frameCount = 0;
        private double _currentFps = 0;

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
        
        // FPS actual para mostrar en UI
        public double CurrentFps => _currentFps;

        public MainViewModel(IWebRTCService webRTCService)
        {
            _webRTCService = webRTCService;

            _webRTCService.OnConnectionStateChanged += (state) => 
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() => StatusText = state);
            };
            
            // Usar el nuevo evento optimizado con raw frames
            _webRTCService.OnRawFrameReceived += HandleRawFrame;
            
            // Fallback al evento legacy por compatibilidad
            _webRTCService.OnVideoFrameReceived += HandleVideoFrame;
            
            _webRTCService.OnScreensInfoReceived += (screens) => 
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() => RemoteScreens = screens);
            };
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

                bool success = await _webRTCService.StartConnectionAsync(hostId);
                
                if (success)
                {
                    Debug.WriteLine("[ViewModel] WebRTCService: conexión exitosa");
                    IsConnected = true;
                    StatusText = "Conectado";
                }
                else
                {
                    Debug.WriteLine("[ViewModel] WebRTCService: falló la conexión");
                    StatusText = "Error: No se pudo conectar";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewModel] ERROR al conectar: {ex}");
                StatusText = $"Error: {ex.Message}";
            }
        }

        public async Task Disconnect()
        {
            try
            {
                await _webRTCService.DisposeAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewModel] Error al desconectar: {ex.Message}");
            }
            finally
            {
                IsConnected = false;
                CurrentFrame = null;
                _writeableBitmap = null;
                _lastWidth = 0;
                _lastHeight = 0;
                StatusText = "Desconectado.";
                _frameCount = 0;
                _currentFps = 0;
            }
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

        /// <summary>
        /// Maneja frames raw BGRA usando WriteableBitmap para máxima eficiencia.
        /// Evita crear nuevos objetos BitmapImage por cada frame.
        /// </summary>
        private void HandleRawFrame(RawVideoFrame frame)
        {
            // Calcular FPS
            UpdateFpsStats();
            
            try
            {
                // Crear o reutilizar WriteableBitmap
                if (_writeableBitmap == null || 
                    _lastWidth != frame.Width || 
                    _lastHeight != frame.Height)
                {
                    // Solo crear nuevo bitmap si las dimensiones cambiaron
                    _writeableBitmap = new WriteableBitmap(
                        frame.Width, 
                        frame.Height, 
                        96, 96, 
                        PixelFormats.Bgra32, 
                        null);
                    _lastWidth = frame.Width;
                    _lastHeight = frame.Height;
                    
                    Debug.WriteLine($"[ViewModel] Created new WriteableBitmap: {frame.Width}x{frame.Height}");
                }
                
                // Copiar píxeles directamente al buffer del WriteableBitmap
                _writeableBitmap.WritePixels(
                    new Int32Rect(0, 0, frame.Width, frame.Height),
                    frame.Pixels,
                    frame.Stride,
                    0);
                
                // Actualizar el frame actual (WriteableBitmap se actualiza in-place)
                if (CurrentFrame != _writeableBitmap)
                {
                    CurrentFrame = _writeableBitmap;
                }
                else
                {
                    // Forzar actualización de UI aunque sea el mismo objeto
                    OnPropertyChanged(nameof(CurrentFrame));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewModel] Error en HandleRawFrame: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handler legacy para frames BMP. Usado como fallback.
        /// </summary>
        private void HandleVideoFrame(byte[] frameData)
        {
            UpdateFpsStats();

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Render, 
                () => 
            {
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
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ViewModel] Error decodificando frame BMP: {ex.Message}");
                }
            });
        }
        
        private void UpdateFpsStats()
        {
            _frameCount++;
            if (_fpsStopwatch.ElapsedMilliseconds >= 1000)
            {
                _currentFps = _frameCount * 1000.0 / _fpsStopwatch.ElapsedMilliseconds;
                _frameCount = 0;
                _fpsStopwatch.Restart();
                
                Debug.WriteLine($"[ViewModel] Render FPS: {_currentFps:F1}");
                OnPropertyChanged(nameof(CurrentFps));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void SendClipboardData(string text)
        {
            if (!IsConnected || string.IsNullOrEmpty(text)) return;
            var ev = new InputEvent { EventType = "clipboard", ClipboardContent = text };
            _webRTCService.SendInputEvent(ev);
        }
    }
}