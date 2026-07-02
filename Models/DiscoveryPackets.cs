using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using CanvasServer.Models;

namespace CanvasServer.Network
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(ServerBeaconPacket), "beacon")]
    [JsonDerivedType(typeof(ServerGoodbyePacket), "goodbye")]
    public interface IDiscoveryPacket { }

    public sealed record ServerBeaconPacket(
        string ServerId, 
        string MachineName, 
        int TcpPort, 
        IReadOnlyList<RoomInfo> Rooms, 
        int TotalRoomCount, 
        DateTime ServerTimeUtc) : IDiscoveryPacket;

    public sealed record ServerGoodbyePacket(string ServerId) : IDiscoveryPacket;
}
