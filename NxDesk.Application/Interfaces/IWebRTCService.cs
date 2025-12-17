using NxDesk.Application.DTOs;

namespace NxDesk.Application.Interfaces
{
    public interface IWebRTCService
    {
        Task<bool> StartConnectionAsync(string hostId);

        void SendInputEvent(InputEvent inputEvent);
        Task DisposeAsync();

        event Action<string> OnConnectionStateChanged;
        
        event Action<byte[]> OnVideoFrameReceived;
        
        event Action<RawVideoFrame> OnRawFrameReceived;
        
        event Action<List<string>> OnScreensInfoReceived;
    }
}