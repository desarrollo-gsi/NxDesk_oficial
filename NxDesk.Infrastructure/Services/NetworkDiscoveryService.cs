using System.Net;
using System.Net.Sockets;
using System.Text;
using NxDesk.Application.Interfaces;
using NxDesk.Domain.Entities;
using System.Diagnostics;

namespace NxDesk.Infrastructure.Services
{
    public class NetworkDiscoveryService : INetworkDiscoveryService
    {
        private const int DISCOVERY_PORT = 50002;
        private UdpClient _udpListener;
        private readonly string _myId;
        private readonly string _myAlias;

        public event Action<DiscoveredDevice> OnDeviceDiscovered;

        public NetworkDiscoveryService(IIdentityService identityService)
        {
            _myId = identityService.GetMyId();
            _myAlias = identityService.GetMyAlias();
        }

        public void Start()
        {
            Task.Run(StartListening);
            Task.Run(StartBroadcasting);
        }

        private async Task StartListening()
        {
            try
            {
                _udpListener = new UdpClient(DISCOVERY_PORT) { EnableBroadcast = true };
                while (true)
                {
                    var result = await _udpListener.ReceiveAsync();
                    string data = Encoding.UTF8.GetString(result.Buffer);

                    if (data.StartsWith("NXDESK_DISCOVERY:"))
                    {
                        var parts = data.Split(':');
                        if (parts.Length == 3 && parts[1] != _myId) // Ignorarnos a nosotros mismos aquí
                        {
                            OnDeviceDiscovered?.Invoke(new DiscoveredDevice
                            {
                                ConnectionID = parts[1],
                                Alias = parts[2],
                                IPAddress = result.RemoteEndPoint.Address.ToString()
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[Discovery Error] {ex.Message}"); }
        }

        private async Task StartBroadcasting()
        {
            using var broadcaster = new UdpClient { EnableBroadcast = true };
            var target = new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT);
            byte[] data = Encoding.UTF8.GetBytes($"NXDESK_DISCOVERY:{_myId}:{_myAlias}");

            while (true)
            {
                await broadcaster.SendAsync(data, data.Length, target);
                await Task.Delay(5000);
            }
        }
    }
}