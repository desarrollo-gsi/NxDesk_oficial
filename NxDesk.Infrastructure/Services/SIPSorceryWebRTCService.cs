using Newtonsoft.Json;
using NxDesk.Application.DTOs;
using NxDesk.Application.Interfaces;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Media;
// using System.Windows.Media.Imaging; // Ya no es necesario aquí si pasamos bytes

namespace NxDesk.Infrastructure.Services
{
    public class SIPSorceryWebRTCService : IWebRTCService
    {
        private readonly ISignalingService _signalingService;
        private RTCPeerConnection _pc;
        private RTCDataChannel _dataChannel;

        public event Action<string> OnConnectionStateChanged;

        // CORRECCIÓN 1: Ajustado a byte[] para coincidir con tu interfaz IWebRTCService
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

            _pc.onconnectionstatechange += state =>
            {
                // CORRECCIÓN 2: Uso explícito de System.Windows.Application para evitar conflicto de nombres
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    OnConnectionStateChanged?.Invoke(state.ToString());
                });
            };

            _pc.OnRtpPacketReceived += (IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket) =>
            {
                if (mediaType == SDPMediaTypesEnum.video && rtpPacket.Header.PayloadType == 96)
                {
                    var frameData = rtpPacket.Payload;

                    // CORRECCIÓN 3: Enviamos los bytes crudos directamente.
                    // La conversión a BitmapSource debe hacerse en el ViewModel (Capa de Presentación).
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        OnVideoFrameReceived?.Invoke(frameData);
                    });
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