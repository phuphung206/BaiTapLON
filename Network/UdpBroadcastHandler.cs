using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CanvasServer.Core;

namespace CanvasServer.Network;

public sealed class UdpBroadcastHandler : BackgroundService
{
    private const int BeaconIntervalMs  = 2000;
    private const int MaxBeaconRooms    = 8;
    private const int MaxDatagramBytes  = 1400; // an toàn dưới MTU 1500, tránh phân mảnh IP

    private readonly string _serverId = Guid.NewGuid().ToString("N");
    private readonly RoomManager _roomManager;
    private readonly ServerConfig _config;
    private readonly ILogger<UdpBroadcastHandler> _logger;

    public UdpBroadcastHandler(RoomManager roomManager, ServerConfig config, ILogger<UdpBroadcastHandler> logger)
    {
        _roomManager = roomManager;
        _config      = config;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udpClient = new UdpClient();
        udpClient.EnableBroadcast = true; // ⚠ bắt buộc — thiếu dòng này SendAsync sẽ ném SocketException

        var target = new IPEndPoint(IPAddress.Broadcast, _config.UdpDiscoveryPort); // 255.255.255.255 — broadcast giới hạn trong LAN, không bị router forward sang subnet khác

        _logger.LogInformation($"[UDP] Phát beacon mỗi {BeaconIntervalMs}ms tới port {_config.UdpDiscoveryPort}");

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(BeaconIntervalMs));
            do
            {
                await SendBeaconAsync(udpClient, target, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Đường tắt gọn gàng — bổ trợ cho TTL sweep ở client (mục 4)
            await SendGoodbyeAsync(udpClient, target);
        }
    }

    private async Task SendBeaconAsync(UdpClient udpClient, IPEndPoint target, CancellationToken ct)
    {
        var allRooms = _roomManager.GetPublicList();
        var rooms    = allRooms.Take(MaxBeaconRooms).ToList();

        var beacon = new ServerBeaconPacket(
            ServerId:       _serverId,
            MachineName:    Environment.MachineName,
            TcpPort:        _config.TcpPort,
            Rooms:          rooms,
            TotalRoomCount: allRooms.Count,
            ServerTimeUtc:  DateTime.UtcNow);

        // ⚠ Phải truyền generic argument <IDiscoveryPacket> tường minh, cùng lý do đã gặp
        // với IPacket ở phần TCP framing: để trình biên dịch suy luận theo kiểu cụ thể
        // ServerBeaconPacket thì "$type" sẽ KHÔNG được ghi ra, phía client deserialize sai.
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes<IDiscoveryPacket>(beacon);

        if (payload.Length > MaxDatagramBytes)
        {
            _logger.LogWarning($"[UDP] Beacon vượt {MaxDatagramBytes} byte ({payload.Length}); bỏ danh sách phòng khỏi gói tin.");
            beacon  = beacon with { Rooms = [] }; // beacon vẫn là ServerBeaconPacket cụ thể tại đây → with hợp lệ
            payload = JsonSerializer.SerializeToUtf8Bytes<IDiscoveryPacket>(beacon);
        }

        try
        {
            await udpClient.SendAsync(payload, target, ct);
        }
        catch (SocketException ex)
        {
            _logger.LogDebug($"[UDP] Gửi beacon thất bại: {ex.Message}"); // lỗi mạng tạm thời — không crash service
        }
    }

    private async Task SendGoodbyeAsync(UdpClient udpClient, IPEndPoint target)
    {
        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes<IDiscoveryPacket>(new ServerGoodbyePacket(_serverId));
            await udpClient.SendAsync(payload, target, CancellationToken.None);
        }
        catch { /* best-effort khi server đang tắt, không có gì để xử lý thêm */ }
    }
}
