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
        
        // Configuración de reintentos
        private const int MAX_RETRY_ATTEMPTS = 3;
        private const int INITIAL_RETRY_DELAY_MS = 1000;

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
                  .WithAutomaticReconnect(new[] { 
                      TimeSpan.FromSeconds(0), 
                      TimeSpan.FromSeconds(2), 
                      TimeSpan.FromSeconds(5), 
                      TimeSpan.FromSeconds(10),
                      TimeSpan.FromSeconds(30)
                  })
                  .Build();

            // Manejo de reconexión
            _hubConnection.Reconnecting += error =>
            {
                Debug.WriteLine($"[SignalR] Reconectando... Error: {error?.Message}");
                return Task.CompletedTask;
            };

            _hubConnection.Reconnected += connectionId =>
            {
                Debug.WriteLine($"[SignalR] Reconectado con ID: {connectionId}");
                // Reincorporarse a la sala después de reconectar
                if (!string.IsNullOrEmpty(_roomId))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _hubConnection.InvokeAsync("JoinRoom", _roomId);
                            Debug.WriteLine($"[SignalR] Reincorporado a la sala: {_roomId}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SignalR] Error al reincorporarse: {ex.Message}");
                        }
                    });
                }
                return Task.CompletedTask;
            };

            _hubConnection.Closed += error =>
            {
                Debug.WriteLine($"[SignalR] Conexión cerrada. Error: {error?.Message}");
                return Task.CompletedTask;
            };

            _hubConnection.On<SdpMessage>("ReceiveMessage", async (message) =>
            {
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
            
            // Implementar reintentos con backoff exponencial
            int attempt = 0;
            int delayMs = INITIAL_RETRY_DELAY_MS;
            
            while (attempt < MAX_RETRY_ATTEMPTS)
            {
                try
                {
                    if (_hubConnection.State == HubConnectionState.Disconnected)
                    {
                        Debug.WriteLine($"[SignalR] Conectando a {_serverUrl}... (Intento {attempt + 1}/{MAX_RETRY_ATTEMPTS})");
                        
                        // Timeout para la conexión
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await _hubConnection.StartAsync(cts.Token);
                        
                        Debug.WriteLine("[SignalR] Conexión establecida.");
                    }

                    await _hubConnection.InvokeAsync("JoinRoom", _roomId);
                    Debug.WriteLine($"[SignalR] Unido a la sala: {_roomId}");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine($"[SignalR] Timeout en conexión (intento {attempt + 1})");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SignalR Error] Intento {attempt + 1}: {ex.Message}");
                }
                
                attempt++;
                if (attempt < MAX_RETRY_ATTEMPTS)
                {
                    Debug.WriteLine($"[SignalR] Reintentando en {delayMs}ms...");
                    await Task.Delay(delayMs);
                    delayMs *= 2; // Backoff exponencial
                }
            }
            
            Debug.WriteLine($"[SignalR] Falló después de {MAX_RETRY_ATTEMPTS} intentos");
            return false;
        }
        public async Task LeaveRoomAsync()
        {
            if (!string.IsNullOrEmpty(_roomId) && _hubConnection.State == HubConnectionState.Connected)
            {
                try
                {
                    // Llamamos al nuevo método del Hub
                    await _hubConnection.InvokeAsync("LeaveRoom", _roomId);
                    Debug.WriteLine($"[SignalR] Saliendo de la sala: {_roomId}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SignalR] Error al salir de sala: {ex.Message}");
                }
                finally
                {
                    // Importante: Limpiamos el ID localmente
                    _roomId = null;
                }
            }
        }

        public async Task RelayMessageAsync(SdpMessage message)
        {
            if (_hubConnection.State == HubConnectionState.Connected)
            {
                try
                {
                    Debug.WriteLine($"[SignalR] Enviando mensaje al Host: {message.Type}");
                    await _hubConnection.InvokeAsync("RelayMessage", _roomId, message);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SignalR] Error enviando mensaje: {ex.Message}");
                }
            }
            else
            {
                Debug.WriteLine($"[SignalR] No se puede enviar mensaje, estado: {_hubConnection.State}");
            }
        }
    }
}