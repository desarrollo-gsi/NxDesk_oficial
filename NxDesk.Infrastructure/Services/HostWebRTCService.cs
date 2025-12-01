using Newtonsoft.Json;
using NxDesk.Application.DTOs;
using NxDesk.Application.Interfaces;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.IO;

namespace NxDesk.Infrastructure.Services
{
    public class HostWebRTCService
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private readonly ISignalingService _signalingService;
        private readonly IInputSimulator _inputSimulator;
        private RTCPeerConnection _peerConnection;
        private bool _isCapturing;
        private int _currentScreenIndex = 0;
        private RTCDataChannel _dataChannel;

        private readonly VpxVideoEncoder _vpxEncoder = new VpxVideoEncoder();

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
                    Log("[Host] Oferta recibida. Configurando conexión...");

                    var config = new RTCConfiguration
                    {
                        iceServers = new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } }
                    };

                    _peerConnection = new RTCPeerConnection(config);

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
                    var offerInit = new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.offer,
                        sdp = offerSdp.ToString()
                    };

                    _peerConnection.setRemoteDescription(offerInit);

                    var answer = _peerConnection.createAnswer(null);
                    await _peerConnection.setLocalDescription(answer);

                    await _signalingService.RelayMessageAsync(new SdpMessage
                    {
                        Type = "answer",
                        Payload = answer.sdp
                    });

                    _isCapturing = true;
                    _ = Task.Run(CaptureLoop);
                }
                else if (message.Type == "ice-candidate")
                {
                    var candidateInit = JsonConvert.DeserializeObject<RTCIceCandidateInit>(message.Payload);
                    if (candidateInit != null)
                    {
                        _peerConnection?.addIceCandidate(candidateInit);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Signaling Error] {ex.Message}");
            }
        }

        private async Task CaptureLoop()
        {
            Log("[Host] Bucle de captura iniciado (Modo Referencia).");

            while (_isCapturing)
            {
                if (_peerConnection?.connectionState == RTCPeerConnectionState.closed ||
                    _peerConnection?.connectionState == RTCPeerConnectionState.failed)
                {
                    Log("[Host] Conexión finalizada.");
                    break;
                }

                if (_peerConnection?.connectionState != RTCPeerConnectionState.connected)
                {
                    await Task.Delay(100);
                    continue;
                }

                var startTime = DateTime.Now;

                try
                {
                    using (var bitmap = CaptureScreenRaw())
                    {
                        if (bitmap != null)
                        {
                            var rawBuffer = BitmapToBytes(bitmap);

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
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Loop Error] {ex.Message}");
                }

                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                var waitTime = 50 - (int)elapsed;
                if (waitTime > 0) await Task.Delay(waitTime);
            }
        }

        private Bitmap CaptureScreenRaw()
        {
            try
            {
                var screens = Screen.AllScreens;
                if (_currentScreenIndex >= screens.Length) _currentScreenIndex = 0;

                var bounds = screens[_currentScreenIndex].Bounds;
                int targetWidth = bounds.Width;
                int targetHeight = bounds.Height;

                if (targetWidth > 1920)
                {
                    float ratio = (float)bounds.Height / bounds.Width;
                    targetWidth = 1920;
                    targetHeight = (int)(targetWidth * ratio);
                }

                if (targetWidth % 2 != 0) targetWidth--;
                if (targetHeight % 2 != 0) targetHeight--;

                var finalBitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);

                using (var g = Graphics.FromImage(finalBitmap))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighSpeed;

                    if (targetWidth == bounds.Width && targetHeight == bounds.Height)
                    {
                        g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                    }
                    else
                    {
                        using (var fullScreenBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
                        using (var gFull = Graphics.FromImage(fullScreenBmp))
                        {
                            gFull.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                            g.DrawImage(fullScreenBmp, 0, 0, targetWidth, targetHeight);
                        }
                    }
                }
                return finalBitmap;
            }
            catch (Exception ex)
            {
                Log($"[CaptureScreenRaw Error] {ex.Message}");
                return null;
            }
        }

        private byte[] BitmapToBytes(Bitmap bmp)
        {
            BitmapData bmpData = null;
            try
            {
                bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);

                int bytesPerPixel = 4;
                int widthInBytes = bmp.Width * bytesPerPixel;
                int size = widthInBytes * bmp.Height;
                byte[] rgbValues = new byte[size];

                for (int y = 0; y < bmp.Height; y++)
                {
                    IntPtr rowPtr = IntPtr.Add(bmpData.Scan0, y * bmpData.Stride);
                    Marshal.Copy(rowPtr, rgbValues, y * widthInBytes, widthInBytes);
                }

                return rgbValues;
            }
            finally
            {
                if (bmpData != null) bmp.UnlockBits(bmpData);
            }
        }

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
            }
            catch { }
        }

        private void ProcessInputEvent(InputEvent ev)
        {
            if (ev == null) return;
            switch (ev.EventType)
            {
                case "mousemove":
                    _inputSimulator.MoveMouse(ev.X!.Value, ev.Y!.Value, _currentScreenIndex);
                    break;
                case "mousedown":
                    _inputSimulator.Click(ev.Button!, true);
                    break;
                case "mouseup":
                    _inputSimulator.Click(ev.Button!, false);
                    break;
                case "keydown":
                    _inputSimulator.SendKey(ev.Key!, true);
                    break;
                case "keyup":
                    _inputSimulator.SendKey(ev.Key!, false);
                    break;
                case "control" when ev.Command == "switch_screen":
                    _currentScreenIndex = ev.Value ?? 0;
                    SendScreenList();
                    break;
            }
        }

        private void SendScreenList()
        {
            if (_dataChannel?.readyState != RTCDataChannelState.open) return;
            try
            {
                var names = Screen.AllScreens.Select((s, i) => $"Pantalla {i + 1}").ToList();
                var payload = JsonConvert.SerializeObject(new ScreenInfoPayload { ScreenNames = names });
                var msg = new DataChannelMessage { Type = "system:screen_info", Payload = payload };
                _dataChannel.send(JsonConvert.SerializeObject(msg));
            }
            catch { }
        }

        private void Log(string message)
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "host_debug.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}