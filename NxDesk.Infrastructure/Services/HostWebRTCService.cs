using Newtonsoft.Json;
using NxDesk.Application.DTOs;
using NxDesk.Application.Interfaces;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.IO; // <--- SOLUCIÓN ERROR: Path y File

namespace NxDesk.Infrastructure.Services
{
    public class HostWebRTCService
    {
        private readonly ISignalingService _signalingService;
        private readonly IInputSimulator _inputSimulator;
        private RTCPeerConnection _peerConnection;
        private bool _isCapturing;
        private int _currentScreenIndex = 0;
        private RTCDataChannel _dataChannel;
        private uint _timestamp = 0;

        public HostWebRTCService(ISignalingService signalingService, IInputSimulator inputSimulator)
        {
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
            if (message.Type == "offer")
            {
                Log("[Host] Oferta recibida. Creando respuesta...");
                var config = new RTCConfiguration
                {
                    iceServers = new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } }
                };

                _peerConnection = new RTCPeerConnection(config);

                var videoFormats = new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.VP8, 96)),
                    new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.H264, 107))
                };

                var videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, videoFormats, MediaStreamStatusEnum.SendOnly);
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
                            Payload = candidate.ToString()
                        });
                    }
                };

                var offerInit = new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.offer,
                    sdp = message.Payload
                };

                var setResult = _peerConnection.setRemoteDescription(offerInit);

                if (setResult != SetDescriptionResultEnum.OK)
                {
                    Log($"[ERROR] setRemoteDescription falló: {setResult}");
                    return;
                }

                var answer = _peerConnection.createAnswer(null);
                _peerConnection.setLocalDescription(answer);

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

        private async Task CaptureLoop()
        {
            Log("[Host] Transmisión iniciada.");
            while (_isCapturing && _peerConnection?.connectionState == RTCPeerConnectionState.connected)
            {
                try
                {
                    using var bitmap = CaptureScreen();
                    if (bitmap == null) continue;

                    var buffer = BitmapToBytes(bitmap);

                    // --- SOLUCIÓN ERROR 2: GetRtpSender no existe ---
                    // Usamos GetSenders() y filtramos por el tipo de medio.
                    var senders = _peerConnection.GetSenders();
                    var videoSender = senders.FirstOrDefault(s => s.Track?.Kind == SDPMediaTypesEnum.video);

                    if (videoSender != null)
                    {
                        _timestamp += 3600;
                        videoSender.SendRtp(buffer, _timestamp, 1, 96);
                    }
                }
                catch (Exception ex)
                {
                    Log($"[VIDEO ERROR] {ex.Message}");
                }
                await Task.Delay(40);
            }
            Log("[Host] Captura detenida.");
        }

        private Bitmap CaptureScreen()
        {
            var screens = Screen.AllScreens;
            var screen = _currentScreenIndex < screens.Length ? screens[_currentScreenIndex] : screens[0];
            var bounds = screen.Bounds;
            int w = Math.Min(bounds.Width, 1920);
            int h = Math.Min(bounds.Height, 1080);

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        private byte[] BitmapToBytes(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int size = Math.Abs(data.Stride) * bmp.Height;
                var buffer = new byte[size];
                Marshal.Copy(data.Scan0, buffer, 0, size);
                return buffer;
            }
            finally
            {
                bmp.UnlockBits(data);
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
            }
            catch { }
        }

        private void SendScreenList()
        {
            // --- SOLUCIÓN ERROR 3: Comparación de Enum ---
            // readyState es un Enum RTCDataChannelState, no un string
            if (_dataChannel?.readyState != RTCDataChannelState.open) return;

            var names = Screen.AllScreens.Select((s, i) => $"Pantalla {i + 1}").ToList();
            var payload = JsonConvert.SerializeObject(new ScreenInfoPayload { ScreenNames = names });
            var msg = new DataChannelMessage { Type = "system:screen_info", Payload = payload };
            _dataChannel.send(JsonConvert.SerializeObject(msg));
        }

        private void Log(string message)
        {
            try
            {
                // Requiere using System.IO;
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "host_debug.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}