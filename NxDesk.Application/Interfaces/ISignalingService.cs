using NxDesk.Application.DTOs;

namespace NxDesk.Application.Interfaces
{
    public interface ISignalingService
    {
        Task<bool> ConnectAsync(string roomId);
        Task LeaveRoomAsync(); 
        Task RelayMessageAsync(SdpMessage message);
        string? GetConnectionId();

        event Func<SdpMessage, Task> OnMessageReceived;
    }
}