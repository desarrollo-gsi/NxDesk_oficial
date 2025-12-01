using Newtonsoft.Json;
using NxDesk.Application.DTOs;
using NxDesk.Application.Interfaces;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders; 
using System.Diagnostics;
using System.Net;
using System.Text;
using System.IO; 

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
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                }
            };

            _pc = new RTCPeerConnection(config);

            // --- CORRECCIÓN FINAL ---
            // En lugar de addTransceiver (que no existe en esta versión), usamos addTrack
            // configurado explícitamente como "RecvOnly" (Solo Recibir).
            
            // 1. Definimos que aceptamos video VP8 (Payload 96)
            var videoFormats = new List<SDPAudioVideoMediaFormat>
            {
                new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.VP8, 96))
            };

            // 2. Creamos un Track virtual que indica "Quiero recibir video"
            var videoTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.video, 
                false, 
                videoFormats, 
                MediaStreamStatusEnum.RecvOnly
            );

            // 3. Añadimos este track a la conexión
            _pc.addTrack(videoTrack);
            // ------------------------

            _pc.onconnectionstatechange += state =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    OnConnectionStateChanged?.Invoke(state.ToString());
                });
            };

            _pc.OnRtpPacketReceived += (IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket) =>
            {
                if (mediaType == SDPMediaTypesEnum.video && rtpPacket.Header.PayloadType == 96)
                {
                    // Log para confirmar recepción
                    Debug.WriteLine($"[RTP] Frame recibido: {rtpPacket.Payload.Length} bytes");

                    try 
                    {
                        var rawFrames = _vpxDecoder.DecodeVideo(rtpPacket.Payload, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);

                        if (rawFrames != null)
                        {
                            foreach (var sampleObj in rawFrames)
                            {
                                var bmpBytes = CreateBitmapFromPixels(sampleObj.Sample, (int)sampleObj.Width, (int)sampleObj.Height);
                                
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
                        Debug.WriteLine($"[Client Decode Error] {ex.Message}");
                    }
                }
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
                    var json = Encoding.UTF8.GetString(data);
                    try
                    {
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

        private byte[] CreateBitmapFromPixels(byte[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length == 0) return null;
            if (width <= 0 || height <= 0) { width = 1920; height = 1080; }

            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(new char[] { 'B', 'M' });
                    writer.Write(54 + pixels.Length);
                    writer.Write(0);
                    writer.Write(54);
                    writer.Write(40);
                    writer.Write(width);
                    writer.Write(-height); 
                    writer.Write((short)1);
                    writer.Write((short)32);
                    writer.Write(0);
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