namespace CanvasServer.Core;

public sealed class ServerConfig
{
    public int TcpPort          { get; init; } = 11000;
    public int UdpDiscoveryPort { get; init; } = 11001;
}
