using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CanvasClient.Protocol;

namespace CanvasClient.Networking;

/// <summary>
/// Kết nối TCP tới Server. Đối xứng với mô hình phía Server (một Channel gửi đi
/// + một vòng lặp đọc riêng), nhưng đây là bản CHUNG cho toàn bộ IPacket — không
/// biết gì về Canvas/Room/Chat. CollaborativeCanvasControl chỉ là MỘT trong các
/// consumer của sự kiện <see cref="PacketReceived"/>.
///
/// Quan trọng cho người gọi: <see cref="PacketReceived"/> và <see cref="Disconnected"/>
/// nổ ra trên thread nền (thread đọc mạng), KHÔNG phải UI thread. Bất kỳ subscriber
/// nào đụng tới XAML/Win2D đều phải tự marshal qua DispatcherQueue.TryEnqueue.
/// </summary>
public sealed class ServerConnection : IAsyncDisposable
{
    private readonly Channel<IPacket> _sendChannel = Channel.CreateUnbounded<IPacket>();

    private TcpClient?             _tcpClient;
    private NetworkStream?         _stream;
    private CancellationTokenSource? _cts;
    private Task?                  _receiveLoopTask;
    private Task?                  _sendLoopTask;
    private int                    _disposed;

    public event Action<IPacket>?    PacketReceived;
    public event Action<Exception?>? Disconnected; // null = đóng gọn gàng (server chủ động đóng / mình tự đóng)

    public bool IsConnected => _tcpClient?.Connected == true;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        if (_tcpClient is not null)
            throw new InvalidOperationException("ServerConnection đã được kết nối — hãy tạo instance mới cho mỗi kết nối.");

        var client = new TcpClient { NoDelay = true }; // NoDelay: nét vẽ real-time không nên bị Nagle giữ lại
        await client.ConnectAsync(host, port, ct);

        _tcpClient = client;
        _stream    = client.GetStream();
        _cts       = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _receiveLoopTask = ReceiveLoopAsync(_cts.Token);
        _sendLoopTask    = SendLoopAsync(_cts.Token);
    }

    /// <summary>Đưa gói tin vào hàng đợi gửi đi. Không throw nếu kết nối đã đóng — gói tin bị bỏ lặng lẽ.</summary>
    public void Send(IPacket packet) => _sendChannel.Writer.TryWrite(packet);

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var packet in _sendChannel.Reader.ReadAllAsync(ct))
            {
                if (_stream is null) break;
                await PacketFraming.WritePacketAsync(_stream, packet, ct);
            }
        }
        catch (OperationCanceledException) { /* Dispose() yêu cầu dừng — không phải lỗi */ }
        catch (Exception ex)
        {
            RaiseDisconnected(ex);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await PacketFraming.ReadPacketAsync(_stream!, ct);
                if (packet is null)
                {
                    RaiseDisconnected(null); // Server đóng kết nối gọn gàng
                    return;
                }
                PacketReceived?.Invoke(packet);
            }
        }
        catch (OperationCanceledException) { /* Dispose() yêu cầu dừng — không phải lỗi */ }
        catch (Exception ex)
        {
            RaiseDisconnected(ex);
        }
    }

    private void RaiseDisconnected(Exception? ex) => Disconnected?.Invoke(ex);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _cts?.Cancel();
        _sendChannel.Writer.TryComplete();

        if (_receiveLoopTask is not null) await SafeAwaitAsync(_receiveLoopTask);
        if (_sendLoopTask is not null) await SafeAwaitAsync(_sendLoopTask);

        _stream?.Dispose();
        _tcpClient?.Dispose();
        _cts?.Dispose();
    }

    private static async Task SafeAwaitAsync(Task t)
    {
        try { await t.ConfigureAwait(false); }
        catch { /* đang teardown — bỏ qua lỗi phát sinh trong lúc hủy */ }
    }
}
