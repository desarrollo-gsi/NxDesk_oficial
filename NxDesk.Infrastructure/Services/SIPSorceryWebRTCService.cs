using Newtonsoft.Json;
using NxDesk.Application.DTOs;
using NxDesk.Application.Interfaces;
using SIPSorcery.Net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace NxDesk.Infrastructure.Services
{
    public class SIPSorceryWebRTCService : IWebRTCService
    {
        private readonly ISignalingService _signalingService;
        private RTCPeerConnection _peerConnection;
        private RTCDataChannel _dataChannel;

        // Eventos definidos en la interfaz
        public event Action<string> OnConnectionStateChanged;
        public event Action<byte[]> OnVideoFrameReceived;
        public event Action<List<string>> OnScreensInfoReceived;

        public SIPSorceryWebRTCService(ISignalingService signalingService)
        {
            _signalingService = signalingService;

            // Nos suscribimos a los mensajes que llegan del servidor (Answer, ICE Candidates)
            _signalingService.OnMessageReceived += HandleSignalingMessageAsync;
        }

        public async Task<bool> StartConnectionAsync(string hostId)
        {
            Debug.WriteLine($"[Service] Intentando conectar a SignalR...");
            OnConnectionStateChanged?.Invoke("Conectando al servidor...");

            // 1. Intentar conectar a SignalR primero
            bool connected = await _signalingService.ConnectAsync(hostId);

            if (!connected)
            {
                Debug.WriteLine("[Service] FALLÓ la conexión a SignalR (ConnectAsync devolvió false).");
                OnConnectionStateChanged?.Invoke("Error: No se pudo conectar al servidor de señalización.");
                return false;
            }
            Debug.WriteLine("[Service] Conexión SignalR EXITOSA. Configurando WebRTC...");
            OnConnectionStateChanged?.Invoke("Conectado. Iniciando WebRTC...");

            // 2. Configuración de WebRTC (Servidores STUN)
            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                }
            };

            _peerConnection = new RTCPeerConnection(config);

            // 3. Manejar candidatos ICE locales (se generan al crear la oferta)
            _peerConnection.onicecandidate += async (candidate) =>
            {
                if (candidate != null && !string.IsNullOrWhiteSpace(candidate.candidate))
                {
                    var msg = new SdpMessage
                    {
                        Type = "ice-candidate",
                        Payload = candidate.toJSON(),
                        SenderId = _signalingService.GetConnectionId()
                    };
                    await _signalingService.RelayMessageAsync(msg);
                }
            };

            // 4. Monitorear estado de la conexión
            _peerConnection.onconnectionstatechange += (state) =>
            {
                OnConnectionStateChanged?.Invoke($"Estado P2P: {state}");

                if (state == RTCPeerConnectionState.connected)
                {
                    OnConnectionStateChanged?.Invoke("Conexión establecida.");
                }
                else if (state == RTCPeerConnectionState.failed)
                {
                    OnConnectionStateChanged?.Invoke("Fallo en la conexión P2P.");
                }
            };

            // 5. Recibir Video
            _peerConnection.OnVideoFrameReceived += (endpoint, timestamp, frame, format) =>
            {
                // Pasamos el frame crudo (byte[]) hacia la capa de Aplicación/UI
                OnVideoFrameReceived?.Invoke(frame);
            };

            // 6. Crear DataChannel para enviar Inputs (Mouse/Teclado)
            _dataChannel = await _peerConnection.createDataChannel("input-channel");
            SetupDataChannel();

            // 7. Crear la Oferta SDP (Somos el que inicia la llamada)
            var offer = _peerConnection.createOffer(null);
            await _peerConnection.setLocalDescription(offer);

            // 8. Enviar la oferta al Host a través de SignalR
            var sdpMsg = new SdpMessage
            {
                Type = "offer",
                Payload = _peerConnection.localDescription.sdp.ToString(),
                SenderId = _signalingService.GetConnectionId()
            };
            await _signalingService.RelayMessageAsync(sdpMsg);

            return true;
        }

        private void SetupDataChannel()
        {
            _dataChannel.onopen += () =>
            {
                OnConnectionStateChanged?.Invoke("Canal de datos abierto.");
                // Al abrirse, pedimos la lista de pantallas del host
                RequestScreenList();
            };

            _dataChannel.onmessage += (RTCDataChannel channel, DataChannelPayloadProtocols protocol, byte[] data) =>
            {
                // Procesar mensajes que vienen del Host (ej. lista de pantallas)
                var json = Encoding.UTF8.GetString(data);
                try
                {
                    var msg = JsonConvert.DeserializeObject<DataChannelMessage>(json);

                    if (msg?.Type == "system:screen_info")
                    {
                        var info = JsonConvert.DeserializeObject<ScreenInfoPayload>(msg.Payload);
                        if (info != null && info.ScreenNames != null)
                        {
                            OnScreensInfoReceived?.Invoke(info.ScreenNames);
                        }
                    }
                }
                catch
                {
                    // Ignorar mensajes mal formados
                }
            };
        }

        private void RequestScreenList()
        {
            if (_dataChannel != null && _dataChannel.readyState == RTCDataChannelState.open)
            {
                var msg = new DataChannelMessage
                {
                    Type = "system:get_screens",
                    Payload = ""
                };
                _dataChannel.send(JsonConvert.SerializeObject(msg));
            }
        }

        public void SendInputEvent(InputEvent inputEvent)
        {
            if (_dataChannel != null && _dataChannel.readyState == RTCDataChannelState.open)
            {
                var payload = JsonConvert.SerializeObject(inputEvent);
                var wrapper = new DataChannelMessage
                {
                    Type = "input",
                    Payload = payload
                };
                _dataChannel.send(JsonConvert.SerializeObject(wrapper));
            }
        }

        private async Task HandleSignalingMessageAsync(SdpMessage message)
        {
            if (_peerConnection == null) return;

            try
            {
                if (message.Type == "answer")
                {
                    // El Host aceptó nuestra oferta
                    var result = _peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = SDP.ParseSDPDescription(message.Payload).ToString()
                    });
                }
                else if (message.Type == "ice-candidate")
                {
                    // El Host nos envió un candidato de red para conectar
                    var candidate = JsonConvert.DeserializeObject<RTCIceCandidateInit>(message.Payload);
                    _peerConnection.addIceCandidate(candidate);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebRTC Error] {ex.Message}");
            }
        }

        public async Task DisposeAsync()
        {
            if (_dataChannel != null)
            {
                _dataChannel.close();
                _dataChannel = null;
            }

            if (_peerConnection != null)
            {
                _peerConnection.close();
                _peerConnection = null;
            }

            // Desuscribirse del evento para evitar fugas de memoria si se reusa el servicio
            if (_signalingService != null)
            {
                _signalingService.OnMessageReceived -= HandleSignalingMessageAsync;
            }

            await Task.CompletedTask;
        }
    }
}