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

        public event Action<string> OnConnectionStateChanged;
        public event Action<byte[]> OnVideoFrameReceived;
        public event Action<List<string>> OnScreensInfoReceived;

        public SIPSorceryWebRTCService(ISignalingService signalingService)
        {
            _signalingService = signalingService;
            _signalingService.OnMessageReceived += HandleSignalingMessage;
        }

        public async Task<bool> StartConnectionAsync(string hostId)
        {
            OnConnectionStateChanged?.Invoke("Conectando...");
            if (!await _signalingService.ConnectAsync(hostId))
            {
                OnConnectionStateChanged?.Invoke("Error de señalización");
                return false;
            }

            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } }
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
                try
                {
                    // 1. Decodificar VP8 a Píxeles Crudos
                    // Solicitamos Bgra, pero debemos verificar qué nos devuelve realmente por el tamaño
                    var rawSamples = _vpxDecoder.DecodeVideo(frame, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);

                    if (rawSamples != null)
                    {
                        foreach (var sample in rawSamples)
                        {
                            // 2. Crear BMP válido dinámicamente
                            var bmpBytes = CreateBitmapFromPixels(sample.Sample, (int)sample.Width, (int)sample.Height);

                            if (bmpBytes != null)
                            {
                                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                                {
                                    OnVideoFrameReceived?.Invoke(bmpBytes);
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CLIENT DECODE ERROR] {ex.Message}");
                }
            };

            _pc.onconnectionstatechange += state =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    OnConnectionStateChanged?.Invoke(state.ToString());
                });
            };

            _pc.onicecandidate += async candidate =>
            {
                if (candidate != null && !string.IsNullOrWhiteSpace(candidate.candidate))
                {
                    await _signalingService.RelayMessageAsync(new SdpMessage
                    {
                        Type = "ice-candidate",
                        Payload = JsonConvert.SerializeObject(candidate)
                    });
                }
            };

            _dataChannel = await _pc.createDataChannel("input-channel");

            if (_dataChannel != null)
            {
                _dataChannel.onopen += () => RequestScreenList();
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

            await _signalingService.RelayMessageAsync(new SdpMessage
            {
                Type = "offer",
                Payload = offer.sdp
            });

            return true;
        }

        // MÉTODO CORREGIDO Y BLINDADO
        private byte[] CreateBitmapFromPixels(byte[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length == 0 || width <= 0 || height <= 0) return null;

            // Calcular profundidad de color real basada en el tamaño del buffer
            // Si pixels.Length == width * height * 3 -> es 24 bits (RGB)
            // Si pixels.Length == width * height * 4 -> es 32 bits (BGRA)
            int bytesPerPixel = pixels.Length / (width * height);
            short bitsPerPixel = (short)(bytesPerPixel * 8);

            // Validar que sea un formato soportado (24 o 32 bits)
            if (bitsPerPixel != 24 && bitsPerPixel != 32)
            {
                Debug.WriteLine($"[BMP ERROR] Formato de píxel extraño: {bitsPerPixel} bits/pixel. W={width}, H={height}, Len={pixels.Length}");
                // Intento de recuperación: asumir 32 bits y que sobra/falta algo, o retornar null
                // return null; // Descomentar si prefieres no mostrar nada a mostrar basura
                bitsPerPixel = 32; // Fallback a lo estándar
            }

            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                {
                    // 1. File Header (14 bytes)
                    writer.Write((byte)0x42); // 'B'
                    writer.Write((byte)0x4D); // 'M'
                    writer.Write(54 + pixels.Length); // File Size
                    writer.Write(0); // Reserved
                    writer.Write(54); // Offset to pixel data

                    // 2. Info Header (40 bytes)
                    writer.Write(40); // Header Size
                    writer.Write(width);
                    writer.Write(-height); // Top-down
                    writer.Write((short)1); // Planes
                    writer.Write(bitsPerPixel); // Bits per pixel (Calculado dinámicamente)
                    writer.Write(0); // Compression
                    writer.Write(pixels.Length); // Image Size
                    writer.Write(0);
                    writer.Write(0);
                    writer.Write(0);
                    writer.Write(0);

                    // 3. Pixel Data
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
                    var sdpInit = new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = message.Payload
                    };
                    _pc.setRemoteDescription(sdpInit);
                }
                else if (message.Type == "ice-candidate")
                {
                    var candidate = JsonConvert.DeserializeObject<RTCIceCandidateInit>(message.Payload);
                    if (candidate != null) _pc.addIceCandidate(candidate);
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
        }

        public async Task DisposeAsync()
        {
            _dataChannel?.close();
            _pc?.close();
            if (_signalingService != null)
                _signalingService.OnMessageReceived -= HandleSignalingMessage;
            await Task.CompletedTask;
        }
    }
}