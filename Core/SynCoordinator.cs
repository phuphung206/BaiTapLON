using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CanvasServer.Models;

namespace CanvasServer.Core;

public sealed class SyncCoordinator
{
    private const int MaxDisplayNameLength = 24;
    private const int MaxRoomNameLength    = 40;

    private readonly RoomManager   _roomManager;
    private readonly ChatHandler   _chatHandler;
    private readonly IPacketSender _sender;

    public SyncCoordinator(RoomManager roomManager, ChatHandler chatHandler, IPacketSender sender)
    {
        _roomManager = roomManager;
        _chatHandler = chatHandler;
        _sender      = sender;
    }

    //══════════════════════════════════════════════════════════════
    //  JOIN phòng có sẵn
    //══════════════════════════════════════════════════════════════
    public async Task<Room?> HandleJoinRequestAsync(
        ClientSession client, JoinRoomRequestPacket req, CancellationToken ct)
    {
        var room = _roomManager.Find(req.RoomId);
        if (room is null)
        {
            await _sender.SendAsync(client, new JoinRoomRejectedPacket(RoomActionResult.TargetNotFound), ct);
            return null;
        }

        client.DisplayName = ResolveDisplayName(req.DisplayName, client.SessionId);

        var result = room.TryJoin(client, req.Password);
        if (result != RoomActionResult.Success)
        {
            // Không đóng kết nối — client vẫn thử lại được (vd. nhập lại mật khẩu)
            await _sender.SendAsync(client, new JoinRoomRejectedPacket(result), ct);
            return null;
        }

        await SyncClientAsync(client, room, ct);
        return room;
    }

    //══════════════════════════════════════════════════════════════
    //  TẠO phòng mới — đi qua ĐÚNG con đường sync như join,
    //  client không cần code UI riêng cho hai trường hợp.
    //══════════════════════════════════════════════════════════════
    public async Task<Room> HandleCreateRequestAsync(
        ClientSession client, CreateRoomRequestPacket req, CancellationToken ct)
    {
        client.DisplayName = ResolveDisplayName(req.DisplayName, client.SessionId);

        var roomName = SanitizeName(req.RoomName, MaxRoomNameLength);
        if (roomName.Length == 0) roomName = $"Room-{DateTime.UtcNow:HHmmss}";

        var room = _roomManager.Create(roomName, client, req.Password);
        await SyncClientAsync(client, room, ct);
        return room;
    }

    //══════════════════════════════════════════════════════════════
    //  LÕI HANDSHAKE — dùng chung cho join lẫn create
    //══════════════════════════════════════════════════════════════
    private async Task SyncClientAsync(ClientSession client, Room room, CancellationToken ct)
    {
        client.IsSyncing = true; // phòng thủ — xem mục 2

        // 1) Trạng thái phòng: ai đang ở đây, ai là Owner, khóa/mật khẩu chưa
        await _sender.SendAsync(client, new RoomStateInfoPacket(
            room.Id, room.Name, room.HasPassword, room.IsLocked,
            client.SessionId, room.GetMemberInfos()), ct);

        // 2) Lịch sử chat
        await _chatHandler.SendHistoryAsync(client, room, ct);

        // 3) Canvas snapshot — chỉ gồm các nét đang ACTIVE (đã lọc Undo), đúng thứ tự vẽ gốc.
        //    Chụp SAU khi client đã trong _members, nên mọi sự kiện canvas từ giờ tự động
        //    rơi vào DeltaQueue thay vì gửi thẳng (xem điều kiện IsSyncing trong TcpServerHandler).
        var (snapshotSeq, activeStrokes) = room.Canvas.GetSnapshot();
        await _sender.SendAsync(client, new SyncStartPacket(snapshotSeq), ct);

        for (int i = 0; i < activeStrokes.Count; i++)
        {
            bool isLast = i == activeStrokes.Count - 1;
            await _sender.SendAsync(client, new SnapshotChunkPacket(activeStrokes[i], isLast), ct);
            await Task.Yield(); // nhường thread pool — canvas lớn không chặn client khác
        }

        // 4) Vá khoảng trống giữa lúc GetSnapshot() và lúc gửi xong chunk cuối.
        //    Có thể là nét mới (StrokePacket), một lượt Undo hoặc Redo — không chỉ StrokePacket nữa.
        var missed = new List<IPacket>();
        while (client.DeltaQueue.TryDequeue(out var evt)) missed.Add(evt);
        if (missed.Count > 0)
            await _sender.SendAsync(client, new DeltaSyncPacket(missed), ct);

        // 5) Tín hiệu KẾT THÚC — dùng thẳng sequence hiện tại của canvas (đúng bất kể mix
        //    Stroke/Undo/Redo trong lúc đồng bộ), không suy luận từ "có/không có Delta"
        await _sender.SendAsync(client, new SyncCompletePacket(room.Canvas.CurrentSequence), ct);

        // 6) Mở khóa — từ đây client nhận sự kiện canvas trực tiếp như thành viên bình thường
        client.IsSyncing = false;

        // 7) Báo NGƯỜI KHÁC (bản thân client đã biết về mình qua bước 1)
        await _sender.BroadcastAsync(room,
            new MemberJoinedPacket(client.SessionId, client.DisplayName),
            excludeId: client.SessionId, ct);
    }

    private static string ResolveDisplayName(string? raw, string sessionId)
    {
        var clean = SanitizeName(raw, MaxDisplayNameLength);
        return clean.Length > 0 ? clean : $"Guest-{sessionId[..4]}";
    }

    private static string SanitizeName(string? raw, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw.Trim().Length);
        foreach (char c in raw.Trim())
            if (!char.IsControl(c)) sb.Append(c);
        var clean = sb.ToString();
        return clean.Length > maxLen ? clean[..maxLen] : clean;
    }
}
