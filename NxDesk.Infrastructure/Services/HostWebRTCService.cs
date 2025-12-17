using Newtonsoft.Json;
using NxDesk.Application.DTOs;
using NxDesk.Application.Interfaces;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace NxDesk.Infrastructure.Services
{
    public class HostWebRTCService
    {
        // --- IMPORTACIONES NATIVAS PARA FALLBACK GDI ---
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hObject, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hObjectSource, int nXSrc, int nYSrc, int dwRop);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        private const int SRCCOPY = 0x00CC0020;
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        private readonly ISignalingService _signalingService;
        private readonly IInputSimulator _inputSimulator;
        private RTCPeerConnection _peerConnection;
        private bool _isCapturing;
        private int _currentScreenIndex = 0;
        private RTCDataChannel _dataChannel;
        private readonly VpxVideoEncoder _vpxEncoder = new VpxVideoEncoder();
        
        // DXGI Desktop Duplication
        private DesktopDuplicationCapture _dxgiCapture;
        private bool _useDxgi = true;
        private int _dxgiFailCount = 0;
        private const int MAX_DXGI_FAILS = 5;
        
        // Estadísticas
        private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
        private int _frameCount = 0;
        
        // Buffers reutilizables para evitar allocations (mejora FPS)
        private byte[] _captureBuffer;
        private int _lastBufferSize = 0;

        public HostWebRTCService(ISignalingService signalingService, IInputSimulator inputSimulator)
        {
            try { SetProcessDPIAware(); } catch { }
            _signalingService = signalingService;
            _inputSimulator = inputSimulator;
            _signalingService.OnMessageReceived += HandleSignalingMessage;
        }

        public async Task StartAsync(string hostId)
        {
            await _signalingService.ConnectAsync(hostId);
            Log($"[Host] Esperando clientes en sala: {hostId}");
        }

        private async Task HandleSignalingMessage(SdpMessage message)
        {
            try
            {
                if (message.Type == "offer")
                {
                    Log("[Host] Oferta recibida. Iniciando...");
                    var config = new RTCConfiguration
                    {
                        iceServers = new List<RTCIceServer>
                        {
                            // Múltiples servidores STUN para mejor conectividad
                            new() { urls = "stun:stun.l.google.com:19302" },
                            new() { urls = "stun:stun1.l.google.com:19302" },
                            new() { urls = "stun:stun2.l.google.com:19302" },
                            new() { urls = "stun:stun.cloudflare.com:3478" },
                            // Servidor TURN gratuito para NAT estrictos
                            new() 
                            { 
                                urls = "turn:openrelay.metered.ca:80",
                                username = "openrelayproject",
                                credential = "openrelayproject"
                            },
                            new() 
                            { 
                                urls = "turn:openrelay.metered.ca:443",
                                username = "openrelayproject",
                                credential = "openrelayproject"
                            }
                        }
                    };
                    _peerConnection = new RTCPeerConnection(config);

                    // Usamos VP8 estándar
                    var videoFormats = new List<VideoFormat> { new VideoFormat(VideoCodecsEnum.VP8, 96) };
                    var videoTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.SendOnly);
                    _peerConnection.addTrack(videoTrack);

                    _peerConnection.ondatachannel += (dc) =>
                    {
                        _dataChannel = dc;
                        dc.onopen += SendScreenList;
                        dc.onmessage += (channel, protocol, data) => HandleInputData(data);
                    };

                    _peerConnection.onicecandidate += async (candidate) =>
                    {
                        if (candidate?.candidate != null)
                        {
                            await _signalingService.RelayMessageAsync(new SdpMessage
                            {
                                Type = "ice-candidate",
                                Payload = JsonConvert.SerializeObject(candidate)
                            });
                        }
                    };

                    var offerSdp = SDP.ParseSDPDescription(message.Payload);
                    var offerInit = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp.ToString() };
                    _peerConnection.setRemoteDescription(offerInit);

                    var answer = _peerConnection.createAnswer(null);
                    await _peerConnection.setLocalDescription(answer);

                    await _signalingService.RelayMessageAsync(new SdpMessage { Type = "answer", Payload = answer.sdp });

                    _isCapturing = true;
                    _ = Task.Run(CaptureLoop);
                }
                else if (message.Type == "ice-candidate")
                {
                    var candidateInit = JsonConvert.DeserializeObject<RTCIceCandidateInit>(message.Payload);
                    if (candidateInit != null) _peerConnection?.addIceCandidate(candidateInit);
                }
            }
            catch (Exception ex) { Log($"[Signaling Error] {ex.Message}"); }
        }

        private async Task CaptureLoop()
        {
            // Intentar inicializar DXGI primero
            _dxgiCapture = new DesktopDuplicationCapture();
            _useDxgi = _dxgiCapture.Initialize(_currentScreenIndex);
            // Usar siempre GDI por estabilidad (DXGI tiene problemas con frames vacíos)
            Log("[Host] CaptureLoop: Usando GDI para captura estable");
            _useDxgi = false;
            _dxgiCapture?.Dispose();
            _dxgiCapture = null;

            while (_isCapturing)
            {
                if (_peerConnection?.connectionState == RTCPeerConnectionState.closed ||
                    _peerConnection?.connectionState == RTCPeerConnectionState.failed) 
                {
                    Log("[Host] Conexión cerrada o fallida, saliendo del loop");
                    break;
                }

                if (_peerConnection?.connectionState != RTCPeerConnectionState.connected)
                {
                    await Task.Delay(50);
                    continue;
                }

                var startTime = DateTime.Now;
                try
                {
                    using var bitmap = CaptureScreenGDI();
                    if (bitmap != null)
                    {
                        var rawBuffer = BitmapToBytes(bitmap);
                        
                        if (rawBuffer != null && rawBuffer.Length > 0)
                        {
                            var encodedBuffer = _vpxEncoder.EncodeVideo(
                                bitmap.Width,
                                bitmap.Height,
                                rawBuffer,
                                VideoPixelFormatsEnum.Bgra,
                                VideoCodecsEnum.VP8);

                            if (encodedBuffer != null && encodedBuffer.Length > 0)
                            {
                                uint timestamp = (uint)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);
                                _peerConnection.SendVideo(timestamp, encodedBuffer);
                                
                                // Estadísticas de FPS cada 5 segundos
                                _frameCount++;
                                if (_fpsStopwatch.ElapsedMilliseconds >= 5000)
                                {
                                    double fps = _frameCount * 1000.0 / _fpsStopwatch.ElapsedMilliseconds;
                                    Log($"[Host] Capture FPS: {fps:F1}");
                                    _frameCount = 0;
                                    _fpsStopwatch.Restart();
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) 
                { 
                    Log($"[Capture Error] {ex.Message}");
                }

                // Control de FPS (Objetivo: ~30 FPS)
                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                var waitTime = Math.Max(1, 33 - (int)elapsed); // 33ms = 30 FPS
                await Task.Delay(waitTime);
            }
            
            // Cleanup
            _dxgiCapture?.Dispose();
            _dxgiCapture = null;
        }

        /// <summary>
        /// Captura de pantalla con GDI (fallback).
        /// </summary>
        private Bitmap CaptureScreenGDI()
        {
            try
            {
                var screens = Screen.AllScreens;
                if (_currentScreenIndex >= screens.Length) _currentScreenIndex = 0;
                var bounds = screens[_currentScreenIndex].Bounds;

                int width = bounds.Width;
                int height = bounds.Height;

                // VP8 requiere dimensiones pares
                if (width % 2 != 0) width--;
                if (height % 2 != 0) height--;

                IntPtr hDesk = GetDesktopWindow();
                IntPtr hSrce = GetWindowDC(hDesk);
                IntPtr hDest = CreateCompatibleDC(hSrce);
                IntPtr hBmp = CreateCompatibleBitmap(hSrce, width, height);
                IntPtr hOldBmp = SelectObject(hDest, hBmp);

                bool success = BitBlt(hDest, 0, 0, width, height, hSrce, bounds.X, bounds.Y, SRCCOPY);

                Bitmap finalBitmap = null;
                if (success)
                {
                    using (var tmp = Image.FromHbitmap(hBmp))
                    {
                        finalBitmap = tmp.Clone(new Rectangle(0, 0, tmp.Width, tmp.Height), PixelFormat.Format32bppArgb);
                    }
                }

                SelectObject(hDest, hOldBmp);
                DeleteObject(hBmp);
                DeleteDC(hDest);
                ReleaseDC(hDesk, hSrce);

                return finalBitmap;
            }
            catch (Exception ex)
            {
                Log($"[GDI Capture Error] {ex.Message}");
                return null;
            }
        }

        private byte[] BitmapToBytes(Bitmap bmp)
        {
            BitmapData bmpData = null;
            try
            {
                bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
                int bytes = Math.Abs(bmpData.Stride) * bmp.Height;
                
                // Reutilizar buffer si el tamaño es el mismo
                if (_captureBuffer == null || _lastBufferSize != bytes)
                {
                    _captureBuffer = new byte[bytes];
                    _lastBufferSize = bytes;
                }
                
                Marshal.Copy(bmpData.Scan0, _captureBuffer, 0, bytes);
                return _captureBuffer;
            }
            finally
            {
                if (bmpData != null) bmp.UnlockBits(bmpData);
            }
        }

        // --- MANEJO DE INPUTS (MOUSE/TECLADO) ---
        private void HandleInputData(byte[] data)
        {
            try
            {
                var json = Encoding.UTF8.GetString(data);
                var wrapper = JsonConvert.DeserializeObject<DataChannelMessage>(json);
                if (wrapper?.Type == "input")
                {
                    var ev = JsonConvert.DeserializeObject<InputEvent>(wrapper.Payload);
                    ProcessInputEvent(ev);
                }
                else if (wrapper?.Type == "system:get_screens")
                {
                    SendScreenList();
                }
            }
            catch { }
        }

        private void ProcessInputEvent(InputEvent ev)
        {
            if (ev == null) return;

            if (ev.EventType == "mousemove" && ev.X.HasValue && ev.Y.HasValue)
                _inputSimulator.MoveMouse(ev.X.Value, ev.Y.Value, _currentScreenIndex);

            else if (ev.EventType == "mousedown" && !string.IsNullOrEmpty(ev.Button))
                _inputSimulator.Click(ev.Button, true);

            else if (ev.EventType == "mouseup" && !string.IsNullOrEmpty(ev.Button))
                _inputSimulator.Click(ev.Button, false);

            else if (ev.EventType == "keydown" && !string.IsNullOrEmpty(ev.Key))
                _inputSimulator.SendKey(ev.Key, true);

            else if (ev.EventType == "keyup" && !string.IsNullOrEmpty(ev.Key))
                _inputSimulator.SendKey(ev.Key, false);

            else if (ev.EventType == "mousewheel" && ev.Delta.HasValue)
                _inputSimulator.Scroll((int)ev.Delta.Value);

            else if (ev.EventType == "control" && ev.Command == "switch_screen")
            { 
                _currentScreenIndex = ev.Value ?? 0;
                Log($"[Host] Cambiando a pantalla {_currentScreenIndex}");
                SendScreenList(); 
            }

            else if (ev.EventType == "clipboard" && !string.IsNullOrEmpty(ev.ClipboardContent))
            {
                Thread t = new Thread(() => { try { Clipboard.SetText(ev.ClipboardContent); } catch { } });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
            }
        }

        private void SendScreenList()
        {
            if (_dataChannel?.readyState != RTCDataChannelState.open) return;
            try
            {
                var names = Screen.AllScreens.Select((s, i) => $"Pantalla {i + 1}").ToList();
                var msg = new DataChannelMessage { Type = "system:screen_info", Payload = JsonConvert.SerializeObject(new ScreenInfoPayload { ScreenNames = names }) };
                _dataChannel.send(JsonConvert.SerializeObject(msg));
            }
            catch { }
        }

        private void Log(string message)
        {
            try 
            { 
                System.Diagnostics.Debug.WriteLine(message);
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "host_service.log"), $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}"); 
            } catch { }
        }
    }
}