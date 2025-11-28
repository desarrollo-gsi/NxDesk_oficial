using NxDesk.Domain.Entities;

namespace NxDesk.Application.Interfaces
{
    public interface INetworkDiscoveryService
    {
        void Start();
        event Action<DiscoveredDevice> OnDeviceDiscovered;
    }
}