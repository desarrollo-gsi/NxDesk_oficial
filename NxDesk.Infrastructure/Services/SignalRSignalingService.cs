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

            Debug.WriteLine($"[SignalR Config] URL leída de appsettings: '{configUrl}'");

            _serverUrl = !string.IsNullOrEmpty(configUrl) ? configUrl : "http://localhost:5000/signalinghub";

            Debug.WriteLine($"[SignalR Config] URL final a usar: '{_serverUrl}'");

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
                    Debug.WriteLine("[SignalR] Iniciando conexión...");
                    await _hubConnection.StartAsync();
                    Debug.WriteLine("[SignalR] Conexión establecida.");
                }

                await _hubConnection.InvokeAsync("JoinRoom", _roomId);
                Debug.WriteLine($"[SignalR] Unido a la sala: {_roomId}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SignalR Error] Fallo al conectar a {_serverUrl}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Debug.WriteLine($"[SignalR Error Inner] {ex.InnerException.Message}");
                }
                return false;
            }
        }        public async Task RelayMessageAsync(SdpMessage message)
        {
            if (_hubConnection.State == HubConnectionState.Connected)
            {
                await _hubConnection.InvokeAsync("RelayMessage", _roomId, message);
            }
        }
    }
}