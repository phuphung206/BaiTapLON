using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CanvasServer.Core;
using CanvasServer.Models;
using System.Buffers.Binary;
using System.IO;


namespace CanvasServer.Network;

public static class PacketFraming
{
    public const int MaxFrameSize = 16 * 1024 * 1024; // 16 MB — chặn DoS từ length giả mạo

    public static async Task<IPacket?> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] lenBuffer = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(lenBuffer, ct);
        }
        catch (EndOfStreamException)
        {
            return null; // peer đóng kết nối gọn gàng, giữa hai frame — không phải lỗi
        }

        int frameLength = BinaryPrimitives.ReadInt32BigEndian(lenBuffer);
        if (frameLength <= 0 || frameLength > MaxFrameSize)
            throw new InvalidDataException($"Kích thước frame không hợp lệ: {frameLength}");

        byte[] payload = new byte[frameLength];
        await stream.ReadExactlyAsync(payload, ct); // EOF ở đây = đứt giữa chừng — KHÔNG bắt riêng, để propagate

        return JsonSerializer.Deserialize<IPacket>(payload);
    }

    public static async Task WritePacketAsync(NetworkStream stream, IPacket packet, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes<IPacket>(packet); // ⚠ xem mục 3 — phải dùng generic <IPacket>

        if (payload.Length > MaxFrameSize)
            throw new InvalidOperationException("Payload vượt quá giới hạn frame cho phép.");

        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);

        await stream.WriteAsync(frame, ct);
    }
}

public class TcpServerHandler : BackgroundService
{
    private const int HeartbeatIntervalSeconds = 15;
    private const int HeartbeatTimeoutSeconds  = 35;

    private readonly int _port;
    private readonly RoomManager _roomManager;
    private readonly ChatHandler     _chatHandler;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly IPacketSender   _sender;
    private readonly ILogger<TcpServerHandler> _logger;
    private readonly TcpListener _listener;

    public TcpServerHandler(RoomManager roomManager, ChatHandler chatHandler, SyncCoordinator syncCoordinator, IPacketSender sender, ILogger<TcpServerHandler> logger, ServerConfig config)
    {
        _roomManager     = roomManager;
        _chatHandler     = chatHandler;
        _syncCoordinator = syncCoordinator;
        _sender          = sender;
        _logger          = logger;
        _port            = config.TcpPort; // trước đây field này bị hardcode 11000, lệch với config.TcpPort thật sự dùng để bind bên dưới
        _listener        = new TcpListener(IPAddress.Any, config.TcpPort);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Start();
        _logger.LogInformation($"[TCP Server] Đang lắng nghe trên cổng {_port}...");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient tcpClient = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(tcpClient, stoppingToken); // mỗi client một task độc lập
            }
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken serverCt)
    {
        var session = new ClientSession
        {
            DisplayName = "Guest",
            Connection  = tcpClient,
            IpAddress   = ((IPEndPoint)tcpClient.Client.RemoteEndPoint!).Address.ToString()
        };
        _logger.LogInformation($"[TCP] Client connected: {session.SessionId}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        var ct = cts.Token; // riêng cho connection này — hủy khi client này ngắt

        Room? room = null;
        var writerTask    = WriterLoopAsync(session, serverCt); // drain hết hàng đợi rồi mới thoát
        var heartbeatTask = HeartbeatLoopAsync(session, ct);    // dừng ngay khi client ngắt

        try
        {
            var stream = session.Stream;
            while (true)
            {
                IPacket? packet = await PacketFraming.ReadPacketAsync(stream, ct);
                if (packet is null) break; // peer đóng kết nối gọn gàng
                session.LastActivityAt = DateTime.UtcNow;
                room = await DispatchAsync(session, room, packet, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException)       { }
        catch (ObjectDisposedException)    { } // thường do heartbeat tự Close()
        catch (IOException)                { } // socket reset / mất mạng
        catch (InvalidDataException ex)
        {
            _logger.LogWarning($"[TCP] Vi phạm protocol từ {session.SessionId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[TCP] Lỗi không mong đợi với {session.SessionId}");
        }
        finally
        {
            if (room is not null)
                await HandleLeaveOrDisconnectAsync(session, room, CancellationToken.None);
            session.Outbox.Complete(); // cho WriterLoop flush nốt rồi tự thoát
            cts.Cancel();              // dừng HeartbeatLoop ngay
            await Task.WhenAll(writerTask, heartbeatTask).ConfigureAwait(false);
            tcpClient.Dispose();
            _logger.LogInformation($"[TCP] {session.SessionId} đã ngắt kết nối.");
        }
    }

    private async Task<Room?> DispatchAsync(ClientSession session, Room? room, IPacket packet, CancellationToken ct)
    {
        switch (packet)
        {
            case PongPacket:
                return room; // LastActivityAt đã cập nhật ở vòng lặp ngoài
            case JoinRoomRequestPacket req when room is null:
                return await _syncCoordinator.HandleJoinRequestAsync(session, req, ct) ?? room;
            case CreateRoomRequestPacket req when room is null:
                return await _syncCoordinator.HandleCreateRequestAsync(session, req, ct);

            case KickRequestPacket req when room is not null:
                await HandleKickAsync(session, room, req, ct);
                return room;
            case BanRequestPacket req when room is not null:
                await HandleBanAsync(session, room, req, ct);
                return room;
            case SetRoomLockRequestPacket req when room is not null:
                await HandleSetLockAsync(session, room, req, ct);
                return room;
            case ChangePasswordRequestPacket req when room is not null:
                await HandleChangePasswordAsync(session, room, req, ct);
                return room;
            case SendChatMessagePacket req when room is not null:
                await _chatHandler.HandleIncomingAsync(session, room, req, ct);
                return room;
            case StrokePacket req when room is not null:
                await HandleIncomingStrokeAsync(session, room, req.StrokeData, ct);
                return room;
            case UndoRequestPacket when room is not null:
                await HandleUndoRequestAsync(session, room, ct);
                return room;
            case RedoRequestPacket when room is not null:
                await HandleRedoRequestAsync(session, room, ct);
                return room;
            default:
                await _sender.SendAsync(session, new ErrorPacket($"Packet không hợp lệ ở trạng thái hiện tại: {packet.GetType().Name}"), ct);
                return room;
        }
    }

    private async Task WriterLoopAsync(ClientSession session, CancellationToken serverCt)
    {
        try
        {
            await foreach (var packet in session.Inbox.ReadAllAsync(serverCt))
            {
                // Khoá chung với SendThenCloseAsync bên dưới — từ giờ có 2 nguồn ghi vào
                // cùng 1 NetworkStream (writer loop bình thường + gói "lời chào tạm biệt"
                // khi kick/ban), nên bắt buộc phải tuần tự hoá để không xé khung gói tin.
                await session.WriteLock.WaitAsync(serverCt);
                try
                {
                    await PacketFraming.WritePacketAsync(session.Stream, packet, serverCt);
                }
                finally { session.WriteLock.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning($"[TCP] Writer loop lỗi cho {session.SessionId}: {ex.Message}");
        }
    }

    // Gửi gói "lời chào tạm biệt" (kick/ban) và ĐỢI nó thực sự ra khỏi socket rồi mới đóng
    // kết nối. Trước đây gọi _sender.SendAsync(...) (chỉ enqueue, fire-and-forget) rồi đóng
    // Connection ngay dòng kế tiếp — WriterLoop chưa chắc đã kịp dequeue/flush gói tin trước
    // khi socket bị đóng, nên client bị kick/ban có thể KHÔNG BAO GIỜ nhận được lý do
    // (bắt được bằng test end-to-end: "Cannot access a disposed object" trong writer loop).
    private async Task SendThenCloseAsync(ClientSession target, IPacket farewellPacket, CancellationToken ct)
    {
        try
        {
            await target.WriteLock.WaitAsync(ct);
            try
            {
                await PacketFraming.WritePacketAsync(target.Stream, farewellPacket, ct);
            }
            finally { target.WriteLock.Release(); }
        }
        catch { /* target có thể đã tự ngắt kết nối ngay trước đó — bỏ qua, vẫn đóng bên dưới */ }

        try { target.Connection.Close(); }
        catch { /* đã đóng rồi cũng không sao */ }
    }

    private async Task HeartbeatLoopAsync(ClientSession session, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var silentFor = DateTime.UtcNow - session.LastActivityAt;
                if (silentFor.TotalSeconds > HeartbeatTimeoutSeconds)
                {
                    _logger.LogWarning($"[Heartbeat] {session.SessionId} im lặng quá {HeartbeatTimeoutSeconds}s — đóng kết nối.");
                    session.Connection.Close(); // read loop sẽ ném exception → kích hoạt cleanup
                    return;
                }
                session.Enqueue(new PingPacket(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task HandleKickAsync(ClientSession requestor, Room room, KickRequestPacket req, CancellationToken ct)
    {
        var (result, target) = room.TryKick(requestor.SessionId, req.TargetSessionId);
        if (result != RoomActionResult.Success || target is null)
        {
            await _sender.SendAsync(requestor, new ErrorPacket(result.ToString()), ct);
            return;
        }

        await SendThenCloseAsync(target, new YouWereKickedPacket("Kicked by owner"), ct);
        await _sender.BroadcastAsync(room, new MemberKickedPacket(target.SessionId, target.DisplayName), null, ct);
        await _chatHandler.BroadcastSystemAsync(room, SystemMessages.Kicked(target.DisplayName), ct);
        _chatHandler.OnSessionEnded(target.SessionId);
    }

    public async Task HandleBanAsync(ClientSession requestor, Room room, BanRequestPacket req, CancellationToken ct)
    {
        var (result, target) = room.TryBan(requestor.SessionId, req.TargetSessionId);
        if (result != RoomActionResult.Success || target is null)
        {
            await _sender.SendAsync(requestor, new ErrorPacket(result.ToString()), ct);
            return;
        }

        await SendThenCloseAsync(target, new YouWereBannedPacket("Banned by owner"), ct);
        await _sender.BroadcastAsync(room, new MemberBannedPacket(target.SessionId, target.DisplayName), null, ct);
        await _chatHandler.BroadcastSystemAsync(room, SystemMessages.Banned(target.DisplayName), ct);
        _chatHandler.OnSessionEnded(target.SessionId);
    }

    private async Task HandleSetLockAsync(ClientSession requestor, Room room, SetRoomLockRequestPacket req, CancellationToken ct)
    {
        var result = room.TrySetLocked(requestor.SessionId, req.IsLocked);
        if (result != RoomActionResult.Success)
        {
            await _sender.SendAsync(requestor, new ErrorPacket(result.ToString()), ct);
            return;
        }
        await _sender.BroadcastAsync(room, new RoomLockedPacket(req.IsLocked), null, ct);
        await _chatHandler.BroadcastSystemAsync(room, SystemMessages.RoomLocked(req.IsLocked), ct);
    }

    private async Task HandleChangePasswordAsync(ClientSession requestor, Room room, ChangePasswordRequestPacket req, CancellationToken ct)
    {
        var result = room.TryChangePassword(requestor.SessionId, req.NewPassword);
        if (result != RoomActionResult.Success)
        {
            await _sender.SendAsync(requestor, new ErrorPacket(result.ToString()), ct);
            return;
        }
        bool hasPassword = !string.IsNullOrEmpty(req.NewPassword);
        await _sender.BroadcastAsync(room, new RoomPasswordChanged(hasPassword), null, ct);
        await _chatHandler.BroadcastSystemAsync(room, SystemMessages.PasswordChanged(hasPassword), ct);
    }

    public async Task HandleLeaveOrDisconnectAsync(ClientSession session, Room room, CancellationToken ct)
    {
        var (result, newOwner) = room.TryLeave(session.SessionId);

        // TargetNotFound ở đây nghĩa là session này đã bị Kick/Ban xử lý (và thông báo) từ
        // trước rồi (đóng Connection sau Kick/Ban khiến read loop của CHÍNH nó ném exception,
        // rơi vào finally và gọi hàm này lần nữa) — bỏ qua để tránh bắn thêm một "đã rời
        // phòng" chồng chéo, mâu thuẫn với "đã bị kick/ban" vừa gửi trước đó.
        if (result != RoomActionResult.TargetNotFound)
        {
            if (newOwner is not null)
            {
                await _sender.BroadcastAsync(room, new OwnerChangedPacket(newOwner.SessionId, newOwner.DisplayName), null, ct);
                await _chatHandler.BroadcastSystemAsync(room, SystemMessages.NewOwner(newOwner.DisplayName), ct);
            }

            // Trước đây gói MemberLeftPacket bị broadcast 2 lần (1 lần excludeId:null, 1 lần excludeId:session.SessionId
            // ngay bên dưới) — client sẽ thấy thông báo "X đã rời phòng" lặp lại. Giờ chỉ còn đúng 1 lần.
            await _sender.BroadcastAsync(room, new MemberLeftPacket(session.SessionId, session.DisplayName), null, ct);
            await _chatHandler.BroadcastSystemAsync(room, SystemMessages.Left(session.DisplayName), ct);
            _chatHandler.OnSessionEnded(session.SessionId);
        }

        if (room.IsEmpty) _roomManager.TryDelete(room.Id);
    }

    public async Task HandleIncomingStrokeAsync(ClientSession sender, Room room, byte[] rawStrokeData, CancellationToken ct)
    {
        if (sender.IsSyncing) return;
        var packet = room.Canvas.AppendStroke(sender.SessionId, rawStrokeData);
        await FanOutCanvasEventAsync(room, sender.SessionId, packet, ct);
    }

    public async Task HandleUndoRequestAsync(ClientSession sender, Room room, CancellationToken ct)
    {
        if (sender.IsSyncing) return;
        var undone = room.Canvas.UndoLast(sender.SessionId);
        if (undone is null)
        {
            await _sender.SendAsync(sender, new ErrorPacket("Không còn nét nào của bạn để hoàn tác."), ct);
            return;
        }

        var evt = new StrokeUndonePacket(undone.Value.OwnerSessionId, undone.Value.Sequence);
        // Người thực hiện Undo cũng cần gói xác nhận này để tự cập nhật canvas cục bộ của họ
        await _sender.SendAsync(sender, evt, ct);
        await FanOutCanvasEventAsync(room, sender.SessionId, evt, ct);
    }

    public async Task HandleRedoRequestAsync(ClientSession sender, Room room, CancellationToken ct)
    {
        if (sender.IsSyncing) return;
        var redone = room.Canvas.RedoLast(sender.SessionId);
        if (redone is null)
        {
            await _sender.SendAsync(sender, new ErrorPacket("Không còn nét nào của bạn để làm lại."), ct);
            return;
        }

        var evt = new StrokeRedonePacket(redone);
        await _sender.SendAsync(sender, evt, ct);
        await FanOutCanvasEventAsync(room, sender.SessionId, evt, ct);
    }

    // Gửi một sự kiện canvas (stroke mới / undo / redo) tới các thành viên khác trong phòng.
    // Thành viên đang IsSyncing thì được đệm vào DeltaQueue thay vì gửi thẳng, để không chen
    // ngang giữa lúc họ nhận Snapshot (xem SyncCoordinator.SyncClientAsync bước 3-4).
    private async Task FanOutCanvasEventAsync(Room room, string excludeSessionId, IPacket canvasEvent, CancellationToken ct)
    {
        var members = room.GetAllMembers();
        var tasks = new List<Task>();
        foreach (var member in members)
        {
            if (member.SessionId == excludeSessionId) continue;
            if (member.IsSyncing)
                member.DeltaQueue.Enqueue(canvasEvent);
            else
                tasks.Add(_sender.SendAsync(member, canvasEvent, ct));
        }
        await Task.WhenAll(tasks);
    }
}
