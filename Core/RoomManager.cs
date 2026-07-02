using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CanvasServer.Models;

namespace CanvasServer.Core;

public sealed class RoomManager
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();

    public Room Create(string name, ClientSession creator, string? password = null)
    {
        var room = new Room(name, creator, password);
        _rooms[room.Id] = room;
        return room;
    }

    public Room? Find(string roomId) => _rooms.GetValueOrDefault(roomId);

    public bool TryDelete(string roomId)
    {
        if (!_rooms.TryRemove(roomId, out var room)) return false;
        room.Dispose();
        return true;
    }

    public IReadOnlyList<RoomInfo> GetPublicList() => [.._rooms.Values.Select(r => r.ToInfo())];
}
