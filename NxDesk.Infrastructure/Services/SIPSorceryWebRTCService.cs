using Newtonsoft.Json;
using NxDesk.Application.DTOs;
using NxDesk.Application.Interfaces;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace NxDesk.Infrastructure.Services
{
    public class SIPSorceryWebRTCService : IWebRTCService
    {
        private readonly ISignalingService _signalingService;
        private RTCPeerConnection _pc;
        private RTCDataChannel _dataChannel;
        private readonly VpxVideoEncoder _vpxDecoder = new VpxVideoEncoder();
        
        // Eventos
        public event Action<string> OnConnectionStateChanged;
        public event Action<byte[]> OnVideoFrameReceived;
        public event Action<RawVideoFrame> OnRawFrameReceived;
        public event Action<List<string>> OnScreensInfoReceived;
        
        private int _decodedFrameCount = 0;
        private readonly Stopwatch _statsStopwatch = Stopwatch.StartNew();
        
        public SIPSorceryWebRTCService(ISignalingService signalingService)
        {
            _signalingService = signalingService;
            _signalingService.OnMessageReceived += HandleSignalingMessage;
        }
        
        public async Task<bool> StartConnectionAsync(string hostId)
        {
            System.Diagnostics.Trace.WriteLine($"[CLIENT] Iniciando conexión a host: {hostId}");
            OnConnectionStateChanged?.Invoke("Conectando...");
            
            if (!await _signalingService.ConnectAsync(hostId))
            {
                System.Diagnostics.Trace.WriteLine("[CLIENT] Error: No se pudo conectar al SignalingServer");
                OnConnectionStateChanged?.Invoke("Error de señalización");
                return false;
            }
            System.Diagnostics.Trace.WriteLine("[CLIENT] Conectado al SignalingServer");
            
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
            
            _pc = new RTCPeerConnection(config);
            
            var videoFormats = new List<SDPAudioVideoMediaFormat>
            {
                new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.VP8, 96))
            };
            var videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, videoFormats, MediaStreamStatusEnum.RecvOnly);
            _pc.addTrack(videoTrack);
            
            _pc.OnVideoFrameReceived += (endpoint, timestamp, frame, format) =>
            {
                System.Diagnostics.Trace.WriteLine($"[CLIENT] Frame recibido: {frame?.Length ?? 0} bytes, format: {format}");
                
                try
                {
                    if (frame == null || frame.Length == 0)
                    {
                        System.Diagnostics.Trace.WriteLine("[CLIENT] Frame vacío recibido");
                        return;
                    }
                    
                    var rawSamples = _vpxDecoder.DecodeVideo(frame, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);
                    
                    if (rawSamples == null)
                    {
                        System.Diagnostics.Trace.WriteLine("[CLIENT] Decoder retornó null");
                        return;
                    }
                    
                    foreach (var sample in rawSamples)
                    {
                        _decodedFrameCount++;
                        
                        if (_statsStopwatch.ElapsedMilliseconds >= 1000)
                        {
                            System.Diagnostics.Trace.WriteLine($"[CLIENT] Decoded FPS: {_decodedFrameCount}, Frame size: {sample.Width}x{sample.Height}");
                            _decodedFrameCount = 0;
                            _statsStopwatch.Restart();
                        }
                        
                        var bmpBytes = CreateBitmapFromPixels(sample.Sample, (int)sample.Width, (int)sample.Height);
                        if (bmpBytes != null && OnVideoFrameReceived != null)
                        {
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                                System.Windows.Threading.DispatcherPriority.Render,
                                () => OnVideoFrameReceived?.Invoke(bmpBytes));
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[CLIENT DECODE ERROR] {ex.Message}");
                }
            };
            
            _pc.onconnectionstatechange += state =>
            {
                System.Diagnostics.Trace.WriteLine($"[CLIENT] WebRTC Connection state: {state}");
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    OnConnectionStateChanged?.Invoke(state.ToString());
                });
            };
            
            _pc.onicecandidate += async candidate =>
            {
                if (candidate != null && !string.IsNullOrWhiteSpace(candidate.candidate))
                {
                    System.Diagnostics.Trace.WriteLine($"[CLIENT] Enviando ICE candidate");
                    await _signalingService.RelayMessageAsync(new SdpMessage
                    {
                        Type = "ice-candidate",
                        Payload = JsonConvert.SerializeObject(candidate)
                    });
                }
            };
            
            _pc.onicegatheringstatechange += state =>
            {
                System.Diagnostics.Trace.WriteLine($"[CLIENT] ICE gathering state: {state}");
            };
            
            _dataChannel = await _pc.createDataChannel("input-channel");
            if (_dataChannel != null)
            {
                _dataChannel.onopen += () => 
                {
                    System.Diagnostics.Trace.WriteLine("[CLIENT] DataChannel abierto");
                    RequestScreenList();
                };
                _dataChannel.onmessage += (_, _, data) =>
                {
                    try
                    {
                        var json = Encoding.UTF8.GetString(data);
                        var msg = JsonConvert.DeserializeObject<DataChannelMessage>(json);
                        if (msg?.Type == "system:screen_info")
                        {
                            var info = JsonConvert.DeserializeObject<ScreenInfoPayload>(msg.Payload);
                            OnScreensInfoReceived?.Invoke(info?.ScreenNames ?? new List<string>());
                        }
                    }
                    catch { }
                };
            }
            
            var offer = _pc.createOffer(null);
            await _pc.setLocalDescription(offer);
            
            System.Diagnostics.Trace.WriteLine("[CLIENT] Enviando offer al host");
            await _signalingService.RelayMessageAsync(new SdpMessage
            {
                Type = "offer",
                Payload = offer.sdp
            });
            
            return true;
        }

        private byte[] CreateBitmapFromPixels(byte[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length == 0 || width <= 0 || height <= 0) return null;
            
            int bytesPerPixel = pixels.Length / (width * height);
            short bitsPerPixel = (short)(bytesPerPixel * 8);
            
            if (bitsPerPixel != 24 && bitsPerPixel != 32)
            {
                Debug.WriteLine($"[BMP ERROR] Formato inesperado: {bitsPerPixel} bpp. W={width}, H={height}, Len={pixels.Length}");
                bitsPerPixel = 32; 
            }
            
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                {
                    // BMP Header
                    writer.Write((byte)0x42); // 'B'
                    writer.Write((byte)0x4D); // 'M'
                    writer.Write(54 + pixels.Length); // File size
                    writer.Write(0); // Reserved
                    writer.Write(54); // Pixel data offset
                    // DIB Header
                    writer.Write(40); // DIB header size
                    writer.Write(width);
                    writer.Write(-height); // Negative = top-down
                    writer.Write((short)1); // Planes
                    writer.Write(bitsPerPixel);
                    writer.Write(0); // No compression
                    writer.Write(pixels.Length);
                    writer.Write(0);
                    writer.Write(0);
                    writer.Write(0);
                    writer.Write(0);
                    writer.Write(pixels);
                }
                return stream.ToArray();
            }
        }
        
        public void RequestScreenList()
        {
            if (_dataChannel?.readyState == RTCDataChannelState.open)
            {
                var msg = new DataChannelMessage { Type = "system:get_screens", Payload = "" };
                _dataChannel.send(JsonConvert.SerializeObject(msg));
            }
        }
        
        public void SendInputEvent(InputEvent inputEvent)
        {
            if (_dataChannel?.readyState == RTCDataChannelState.open)
            {
                var wrapper = new DataChannelMessage
                {
                    Type = "input",
                    Payload = JsonConvert.SerializeObject(inputEvent)
                };
                _dataChannel.send(JsonConvert.SerializeObject(wrapper));
            }
        }

        private async Task HandleSignalingMessage(SdpMessage message)
        {
            if (_pc == null) return;
            try
            {
                if (message.Type == "answer")
                {
                    System.Diagnostics.Trace.WriteLine("[CLIENT] Answer recibida del host");
                    var sdpInit = new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = message.Payload
                    };
                    _pc.setRemoteDescription(sdpInit);
                    System.Diagnostics.Trace.WriteLine("[CLIENT] Remote description establecida");
                }
                else if (message.Type == "ice-candidate")
                {
                    System.Diagnostics.Trace.WriteLine("[CLIENT] ICE candidate recibido del host");
                    var candidate = JsonConvert.DeserializeObject<RTCIceCandidateInit>(message.Payload);
                    if (candidate != null) _pc.addIceCandidate(candidate);
                }
            }
            catch (Exception ex) 
            { 
                System.Diagnostics.Trace.WriteLine($"[CLIENT Signaling Error] {ex.Message}"); 
            }
        }

        public async Task DisposeAsync()
        {
            try
            {
                _dataChannel?.close();
                _pc?.close();
                if (_signalingService != null)
                    _signalingService.OnMessageReceived -= HandleSignalingMessage;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRTC] Error disposing: {ex.Message}");
            }
            await Task.CompletedTask;
        }
    }
}
