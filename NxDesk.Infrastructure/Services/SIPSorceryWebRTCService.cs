using Newtonsoft.Json;
using NxDesk.Application.DTOs;
using NxDesk.Application.Interfaces;
using SIPSorcery.Net; 

namespace NxDesk.Infrastructure.Services
{
    public class SIPSorceryWebRTCService : IWebRTCService
    {
        private readonly ISignalingService _signalingService;
        private RTCPeerConnection _peerConnection;
        private RTCDataChannel _dataChannel;

        public event Action<string> OnConnectionStateChanged;
        public event Action<byte[]> OnVideoFrameReceived; 
        public event Action<List<string>> OnScreensInfoReceived;

        public SIPSorceryWebRTCService(ISignalingService signalingService)
        {
            _signalingService = signalingService;
            _signalingService.OnMessageReceived += HandleSignalingMessageAsync;
        }

        public async Task StartConnectionAsync(string hostId)
        {
            OnConnectionStateChanged?.Invoke("Conectando SignalR...");
            bool connected = await _signalingService.ConnectAsync(hostId);

            if (!connected)
            {
                OnConnectionStateChanged?.Invoke("Error de conexión");
                return;
            }

            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer> { new RTCIceServer { urls = "stun:stun.l.google.com:19302" } }
            };

            _peerConnection = new RTCPeerConnection(config);

            _peerConnection.onicecandidate += async (candidate) =>
            {
                if (candidate != null && !string.IsNullOrWhiteSpace(candidate.candidate))
                {
                    var msg = new SdpMessage
                    {
                        Type = "ice-candidate",
                        Payload = candidate.toJSON()
                    };
                    await _signalingService.RelayMessageAsync(msg);
                }
            };

            _peerConnection.onconnectionstatechange += (state) =>
                OnConnectionStateChanged?.Invoke($"Estado WebRTC: {state}");

            _peerConnection.OnVideoFrameReceived += (endpoint, timestamp, frame, format) =>
            {
                OnVideoFrameReceived?.Invoke(frame);
            };

            _dataChannel = await _peerConnection.createDataChannel("input-channel");
            SetupDataChannel();

            var offer = _peerConnection.createOffer(null);
            await _peerConnection.setLocalDescription(offer);

            var sdpMsg = new SdpMessage
            {
                Type = "offer",
                Payload = _peerConnection.localDescription.sdp.ToString(),
                SenderId = _signalingService.GetConnectionId()
            };
            await _signalingService.RelayMessageAsync(sdpMsg);
        }

        private void SetupDataChannel()
        {
            _dataChannel.onopen += () =>
            {
                OnConnectionStateChanged?.Invoke("Canal de datos abierto");
                RequestScreenList();
            };

            _dataChannel.onmessage += (RTCDataChannel channel, DataChannelPayloadProtocols protocol, byte[] data) =>
            {
                var json = System.Text.Encoding.UTF8.GetString(data);
                try
                {
                    var msg = JsonConvert.DeserializeObject<DataChannelMessage>(json);
                    if (msg?.Type == "system:screen_info")
                    {
                        var info = JsonConvert.DeserializeObject<ScreenInfoPayload>(msg.Payload);
                        OnScreensInfoReceived?.Invoke(info.ScreenNames);
                    }
                }
                catch { }
            };
        }

        private void RequestScreenList()
        {
            var msg = new DataChannelMessage { Type = "system:get_screens", Payload = "" };
            _dataChannel.send(JsonConvert.SerializeObject(msg));
        }

        public void SendInputEvent(InputEvent inputEvent)
        {
            if (_dataChannel?.readyState == RTCDataChannelState.open)
            {
                var payload = JsonConvert.SerializeObject(inputEvent);
                var wrapper = new DataChannelMessage { Type = "input", Payload = payload };
                _dataChannel.send(JsonConvert.SerializeObject(wrapper));
            }
        }

        private async Task HandleSignalingMessageAsync(SdpMessage message)
        {
            if (_peerConnection == null) return;

            if (message.Type == "answer")
            {
                var result = _peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.answer,
                    sdp = SDP.ParseSDPDescription(message.Payload).ToString()
                });
            }
            else if (message.Type == "ice-candidate")
            {
                var candidate = JsonConvert.DeserializeObject<RTCIceCandidateInit>(message.Payload);
                _peerConnection.addIceCandidate(candidate);
            }
        }

        public async Task DisposeAsync()
        {
            _dataChannel?.close();
            _peerConnection?.close();
        }
    }
}