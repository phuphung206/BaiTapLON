using System.Threading;
using System.Threading.Tasks;
using CanvasServer.Models;

namespace CanvasServer.Core;

public interface IPacketSender
{
    Task SendAsync(ClientSession session, IPacket packet, CancellationToken ct);
    Task BroadcastAsync(Room room, IPacket packet, string? excludeId, CancellationToken ct);
}

public sealed class PacketSender : IPacketSender
{
    public Task SendAsync(ClientSession session, IPacket packet, CancellationToken ct)
    {
        session.Enqueue(packet);
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(Room room, IPacket packet, string? excludeId, CancellationToken ct)
    {
        foreach (var member in room.GetAllMembers())
            if (excludeId is null || member.SessionId != excludeId)
                member.Enqueue(packet);
        return Task.CompletedTask;
    }
}
