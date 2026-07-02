using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using CanvasServer.Models;

namespace CanvasServer.Core;

public sealed class ClientSession
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12].ToUpper();
    public required string DisplayName { get; set; }
    public required string IpAddress   { get; init; }
    public required TcpClient Connection { get; init; }
    public NetworkStream Stream => Connection.GetStream();
    public SemaphoreSlim WriteLock { get; } = new(1, 1);
    public volatile bool IsSyncing = true;

    // Đệm các sự kiện canvas (StrokePacket / StrokeUndonePacket / StrokeRedonePacket) xảy ra
    // TRONG LÚC client này đang nhận Snapshot, để không bỏ lỡ hay lệch thứ tự (xem SyncCoordinator).
    public ConcurrentQueue<IPacket> DeltaQueue { get; } = new();

    // Mốc thời gian hoạt động cuối — heartbeat dựa vào đây để phát hiện chết
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    // Hàng đợi gửi đi — bounded để tránh client chậm ăn hết RAM server
    private readonly Channel<IPacket> _sendChannel = Channel.CreateBounded<IPacket>(
        new BoundedChannelOptions(256)
        {
            FullMode     = BoundedChannelFullMode.DropOldest, // luôn ưu tiên dữ liệu mới nhất
            SingleReader = true,   // chỉ WriterLoop đọc
            SingleWriter = false   // nhiều phần server có thể enqueue
        });
    public ChannelWriter<IPacket> Outbox => _sendChannel.Writer;
    public ChannelReader<IPacket> Inbox  => _sendChannel.Reader;

    public void Enqueue(IPacket packet) => _sendChannel.Writer.TryWrite(packet);

    public MemberInfo ToInfo(bool isOwner) => new(SessionId, DisplayName, isOwner);
}
