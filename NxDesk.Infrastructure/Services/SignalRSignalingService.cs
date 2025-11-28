using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration; 
using NxDesk.Application.DTOs;
using NxDesk.Application.Interfaces;
using System.Diagnostics;

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
            _serverUrl = configuration["SignalR:ServerUrl"] ?? "https://localhost:7099/signalinghub";

            _hubConnection = new HubConnectionBuilder()
                .WithUrl(_serverUrl, options =>
                {
                    options.HttpMessageHandlerFactory = (handler) =>
                    {
                        if (handler is HttpClientHandler clientHandler)
                        {
                            clientHandler.ServerCertificateCustomValidationCallback =
                                (sender, certificate, chain, sslPolicyErrors) => true;
                        }
                        return handler;
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
                    await _hubConnection.StartAsync();
                }

                await _hubConnection.InvokeAsync("JoinRoom", _roomId);
                Debug.WriteLine($"[SignalR] Conectado a la sala: {_roomId}");
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
                await _hubConnection.InvokeAsync("RelayMessage", _roomId, message);
            }
        }
    }
}