using NxDesk.Application.DTOs;

namespace NxDesk.Application.Interfaces
{
    public interface IWebRTCService
    {
        // CAMBIO: Ahora devuelve Task<bool> en lugar de Task
        Task<bool> StartConnectionAsync(string hostId);

        void SendInputEvent(InputEvent inputEvent);
        Task DisposeAsync();

        event Action<string> OnConnectionStateChanged;
        event Action<byte[]> OnVideoFrameReceived;
        event Action<List<string>> OnScreensInfoReceived;
    }
}