using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using NxDesk.Application.DTOs;
using NxDesk.Application.Interfaces;
using System.Diagnostics;
using System.Net.Http;

namespace NxDesk.Infrastructure.Services
{
    public class SignalRSignalingService : ISignalingService
    {
        private readonly string _serverUrl;
        private HubConnection _hubConnection;
        private string _roomId;

        public event Func<SdpMessage, Task> OnMessageReceived;

        public SignalRSignalingService(IConfiguration configuration)
        {
            string configUrl = configuration["SignalR:ServerUrl"];
            // Usamos localhost por defecto si no hay config, asegurando puerto 5000
            _serverUrl = !string.IsNullOrEmpty(configUrl) ? configUrl : "http://localhost:5000/signalinghub";

            _hubConnection = new HubConnectionBuilder()
                  .WithUrl(_serverUrl, options =>
                  {
                      options.HttpMessageHandlerFactory = _ => new HttpClientHandler
                      {
                          ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
                      };
                  })
                    .WithAutomaticReconnect()
                    .Build();

            _hubConnection.On<SdpMessage>("ReceiveMessage", async (message) =>
            {
                // --- LOG DE DEPURACIÓN ---
                Debug.WriteLine($"[SignalR] Mensaje recibido del Host. Tipo: {message.Type}");

                if (OnMessageReceived != null)
                {
                    await OnMessageReceived.Invoke(message);
                }
            });
        }

        public string? GetConnectionId() => _hubConnection?.ConnectionId;

        public async Task<bool> ConnectAsync(string roomId)
        {
            _roomId = roomId;
            try
            {
                if (_hubConnection.State == HubConnectionState.Disconnected)
                {
                    Debug.WriteLine($"[SignalR] Conectando a {_serverUrl}...");
                    await _hubConnection.StartAsync();
                    Debug.WriteLine("[SignalR] Conexión establecida.");
                }

                await _hubConnection.InvokeAsync("JoinRoom", _roomId);
                Debug.WriteLine($"[SignalR] Unido a la sala: {_roomId}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SignalR Error] {ex.Message}");
                return false;
            }
        }

        public async Task RelayMessageAsync(SdpMessage message)
        {
            if (_hubConnection.State == HubConnectionState.Connected)
            {
                Debug.WriteLine($"[SignalR] Enviando mensaje al Host: {message.Type}");
                await _hubConnection.InvokeAsync("RelayMessage", _roomId, message);
            }
        }
    }
}