using Microsoft.AspNetCore.SignalR;
using NxDesk.Application.DTOs;

namespace NxDesk.SignalingServer.Hubs
{
    public class SignalingHub : Hub
    {
        public async Task JoinRoom(string roomId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            await Clients.OthersInGroup(roomId).SendAsync("ParticipantJoined");
        }

        public async Task LeaveRoom(string roomId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        }

        public async Task RelayMessage(string roomId, SdpMessage message)
        {
            await Clients.OthersInGroup(roomId).SendAsync("ReceiveMessage", message);
        }
    }
}