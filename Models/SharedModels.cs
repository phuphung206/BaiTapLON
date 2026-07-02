using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace CanvasServer.Models;

public enum RoomActionResult
{
    Success,
    Unauthorized,
    TargetNotFound,
    InvalidTarget,
    AlreadyBanned,
    RoomLocked,
    WrongPassword
}

public sealed record RoomInfo(string Id, string Name, bool HasPassword, bool IsLocked, int MemberCount, string OwnerName);
public sealed record MemberInfo(string SessionId, string DisplayName, bool IsOwner);

// ──── Packet Protocol ──────────────────────────────────────────
// Trước đây các [JsonDerivedType] này nằm trôi nổi ngay phía trên "enum RoomActionResult"
// (tức là đang gắn nhầm lên một enum) → CS0592, vì JsonPolymorphic/JsonDerivedType chỉ hợp lệ
// trên class/interface. Domain rule đúng: chúng phải gắn thẳng lên interface IPacket bên dưới.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PingPacket),                  "ping")]
[JsonDerivedType(typeof(PongPacket),                  "pong")]
[JsonDerivedType(typeof(KickRequestPacket),           "kick_request")]
[JsonDerivedType(typeof(BanRequestPacket),            "ban_request")]
[JsonDerivedType(typeof(ErrorPacket),                 "error")]
[JsonDerivedType(typeof(YouWereKickedPacket),         "you_were_kicked")]
[JsonDerivedType(typeof(YouWereBannedPacket),         "you_were_banned")]
[JsonDerivedType(typeof(MemberKickedPacket),          "member_kicked")]
[JsonDerivedType(typeof(MemberBannedPacket),          "member_banned")]
[JsonDerivedType(typeof(MemberLeftPacket),            "member_left")]
[JsonDerivedType(typeof(OwnerChangedPacket),          "owner_changed")]
[JsonDerivedType(typeof(RoomLockedPacket),            "room_locked")]
[JsonDerivedType(typeof(RoomPasswordChanged),         "room_password_changed")]
[JsonDerivedType(typeof(SetRoomLockRequestPacket),    "set_room_lock_request")]
[JsonDerivedType(typeof(ChangePasswordRequestPacket), "change_password_request")]
[JsonDerivedType(typeof(StrokePacket),                "stroke")]
[JsonDerivedType(typeof(SyncStartPacket),             "sync_start")]
[JsonDerivedType(typeof(SnapshotChunkPacket),         "snapshot_chunk")]
[JsonDerivedType(typeof(DeltaSyncPacket),             "delta_sync")]
[JsonDerivedType(typeof(MemberJoinedPacket),          "member_joined")]
[JsonDerivedType(typeof(SendChatMessagePacket),       "send_chat")]
[JsonDerivedType(typeof(ChatMessagePacket),           "chat_message")]
[JsonDerivedType(typeof(ChatHistoryPacket),           "chat_history")]
[JsonDerivedType(typeof(JoinRoomRequestPacket),       "join_room_request")]
[JsonDerivedType(typeof(CreateRoomRequestPacket),     "create_room_request")]
[JsonDerivedType(typeof(JoinRoomRejectedPacket),      "join_room_rejected")]
[JsonDerivedType(typeof(RoomStateInfoPacket),         "room_state_info")]
[JsonDerivedType(typeof(SyncCompletePacket),          "sync_complete")]
[JsonDerivedType(typeof(UndoRequestPacket),           "undo_request")]
[JsonDerivedType(typeof(RedoRequestPacket),           "redo_request")]
[JsonDerivedType(typeof(StrokeUndonePacket),          "stroke_undone")]
[JsonDerivedType(typeof(StrokeRedonePacket),          "stroke_redone")]
public interface IPacket { }

// ──── Heartbeat Protocol ───────────────────────────────────────
public sealed record PingPacket(long Timestamp) : IPacket;
public sealed record PongPacket(long Timestamp) : IPacket;

// ──── Room moderation ───────────────────────────────────────────
public sealed record KickRequestPacket(string TargetSessionId) : IPacket;
public sealed record BanRequestPacket(string TargetSessionId) : IPacket;
public sealed record ErrorPacket(string Message) : IPacket;
public sealed record YouWereKickedPacket(string Reason) : IPacket;
public sealed record YouWereBannedPacket(string Reason) : IPacket;
public sealed record MemberKickedPacket(string SessionId, string Name) : IPacket;
public sealed record MemberBannedPacket(string SessionId, string Name) : IPacket;
public sealed record MemberLeftPacket(string SessionId, string Name) : IPacket;
public sealed record OwnerChangedPacket(string NewOwnerSessionId, string NewOwnerName) : IPacket;
public sealed record RoomLockedPacket(bool IsLocked) : IPacket;
public sealed record RoomPasswordChanged(bool HasPassword) : IPacket;

// Client → Server: yêu cầu khóa/mở khóa phòng và đổi mật khẩu (chỉ Owner).
// RoomLockedPacket/RoomPasswordChanged/SystemMessages tương ứng vốn đã có sẵn từ trước
// nhưng chưa từng có gói tin request nào để thực sự kích hoạt chúng.
public sealed record SetRoomLockRequestPacket(bool IsLocked) : IPacket;
public sealed record ChangePasswordRequestPacket(string? NewPassword) : IPacket;

// Tập trung tất cả system message text vào một nơi để dễ bản địa hóa
public static class SystemMessages
{
    public static string Joined(string name)       => $"{name} đã vào phòng.";
    public static string Left(string name)         => $"{name} đã rời phòng.";
    public static string Kicked(string name)       => $"{name} đã bị kick.";
    public static string Banned(string name)       => $"{name} đã bị ban.";
    public static string NewOwner(string name)     => $"{name} trở thành chủ phòng.";
    public static string RoomLocked(bool locked)   => locked ? "Phòng đã bị khóa." : "Phòng đã mở khóa.";
    public static string PasswordChanged(bool has) => has ? "Mật khẩu phòng đã được đặt." : "Mật khẩu phòng đã được xóa.";
    public static string CanvasCleared()           => "Bảng vẽ đã được xóa.";
}

// ──── Canvas Protocol ──────────────────────────────────────────

// Sequence là ID duy nhất, tăng dần của mỗi nét vẽ — dùng chung làm khoá cho cả z-order
// lẫn Undo/Redo. OwnerSessionId được thêm mới để CanvasState biết nét này là của ai
// (bắt buộc phải có để hỗ trợ "Undo/Redo độc lập theo từng người" — trước đây hoàn toàn
// không track quyền sở hữu nét vẽ nên tính năng này không thể tồn tại).
public sealed record StrokePacket(int Sequence, string OwnerSessionId, byte[] StrokeData) : IPacket;

// Báo cho Client biết chuẩn bị nhận Snapshot với Sequence chốt hạ là N
public sealed record SyncStartPacket(int SnapshotSequence) : IPacket;

// Trước đây mỗi chunk chỉ là một byte[] trần, không có Sequence/Owner đi kèm — client nhận
// Snapshot xong sẽ không thể biết nét nào của ai để dựng lại Undo/Redo cục bộ. Giờ mỗi chunk
// tự mô tả đủ bằng cách bọc nguyên một StrokePacket.
public sealed record SnapshotChunkPacket(StrokePacket Stroke, bool IsLastChunk) : IPacket;

// Đệm các sự kiện canvas xảy ra khi client đang giữa chừng nhận Snapshot — có thể là nét
// mới (StrokePacket), một lượt Undo (StrokeUndonePacket) hoặc Redo (StrokeRedonePacket),
// nên kiểu phần tử phải là IPacket chung thay vì ép cứng StrokePacket như trước.
public sealed record DeltaSyncPacket(IReadOnlyList<IPacket> MissedEvents) : IPacket;

public sealed record MemberJoinedPacket(string SessionId, string DisplayName) : IPacket;

// ─── Undo/Redo — độc lập theo từng người (mỗi user chỉ Undo/Redo được nét của chính mình) ───
// Client → Server: không cần field gì, luôn áp dụng cho nét/redo gần nhất của chính sender.
public sealed record UndoRequestPacket : IPacket;
public sealed record RedoRequestPacket : IPacket;

// Server → Client(s): broadcast để mọi người ẩn/vẽ lại đúng nét đã Undo/Redo.
public sealed record StrokeUndonePacket(string OwnerSessionId, int Sequence) : IPacket;
public sealed record StrokeRedonePacket(StrokePacket Stroke) : IPacket;

public enum ChatMessageType { Text, System }

// Immutable record — serialize an toàn qua mạng
public sealed record ChatMessage(
    string           MessageId,   // server-assigned
    string           SenderId,    // SessionId của người gửi, "server" nếu là system
    string           SenderName,  // DisplayName đã được validate
    string           Content,
    DateTime         SentAt,
    ChatMessageType  Type);

// ─── Client → Server ───────────────────────────────────────────────
public sealed record SendChatMessagePacket(string Content) : IPacket;

// ─── Server → Client(s) ────────────────────────────────────────────
// Broadcast real-time khi có tin nhắn mới (user hoặc system)
public sealed record ChatMessagePacket(ChatMessage Message) : IPacket;

// Chỉ gửi một lần cho client MỚI vừa join phòng
public sealed record ChatHistoryPacket(IReadOnlyList<ChatMessage> Messages) : IPacket;

public sealed class ChatHistoryManager : IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Queue<ChatMessage> _history = new();
    private const int MaxHistory = 100;

    // ── User message ────────────────────────────────────────────────
    public ChatMessage AddUserMessage(
        string senderId, string senderName, string content)
        => AddCore(senderId, senderName, content, ChatMessageType.Text);

    // ── System message (server-generated events) ────────────────────
    public ChatMessage AddSystemMessage(string content)
        => AddCore("server", "System", content, ChatMessageType.System);

    // ── Snapshot for new clients ────────────────────────────────────
    public IReadOnlyList<ChatMessage> GetSnapshot()
    {
        _lock.EnterReadLock();
        try { return _history.ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    // ── Internal ────────────────────────────────────────────────────
    private ChatMessage AddCore(
        string senderId, string senderName, string content, ChatMessageType type)
    {
        var msg = new ChatMessage(
            MessageId:  Guid.NewGuid().ToString("N")[..12],
            SenderId:   senderId,
            SenderName: senderName,
            Content:    content,
            SentAt:     DateTime.UtcNow,
            Type:       type);

        _lock.EnterWriteLock();
        try
        {
            _history.Enqueue(msg);
            // Vòng đệm: loại bỏ tin nhắn lâu nhất khi tràn
            if (_history.Count > MaxHistory) _history.Dequeue();
        }
        finally { _lock.ExitWriteLock(); }

        return msg;
    }

    public void Dispose() => _lock.Dispose();
}

public sealed class ChatRateLimiter
{
    private readonly ConcurrentDictionary<string, SessionBucket> _sessions = new();

    private const int  MaxMessages = 5;
    private const long WindowTicks = 3 * TimeSpan.TicksPerSecond; // 3 giây

    public bool TryConsume(string sessionId)
        => _sessions.GetOrAdd(sessionId, _ => new SessionBucket()).TryConsume();

    public void Remove(string sessionId)
        => _sessions.TryRemove(sessionId, out _);

    // ── Inner: per-session sliding window ───────────────────────────
    private sealed class SessionBucket
    {
        private readonly Queue<long> _timestamps = new();
        private readonly object _gate = new();

        public bool TryConsume()
        {
            var now       = DateTime.UtcNow.Ticks;
            var threshold = now - WindowTicks;

            lock (_gate)
            {
                // Loại bỏ timestamps đã ngoài cửa sổ
                while (_timestamps.Count > 0 && _timestamps.Peek() < threshold)
                    _timestamps.Dequeue();

                if (_timestamps.Count >= MaxMessages) return false;

                _timestamps.Enqueue(now);
                return true;
            }
        }
    }
}

// ─── Client → Server ────────────────────────────────────────────
public sealed record JoinRoomRequestPacket  (string RoomId,   string DisplayName, string? Password) : IPacket;
public sealed record CreateRoomRequestPacket(string RoomName, string DisplayName, string? Password) : IPacket;

// ─── Server → Client ────────────────────────────────────────────
public sealed record JoinRoomRejectedPacket(RoomActionResult Reason) : IPacket;

public sealed record RoomStateInfoPacket(
    string RoomId,
    string RoomName,
    bool   HasPassword,
    bool   IsLocked,
    string YourSessionId,                     // client tự biết mình là ai trong danh sách Members
    IReadOnlyList<MemberInfo> Members) : IPacket;

// Gửi VÔ ĐIỀU KIỆN ở bước cuối — điểm neo duy nhất để client Gate UI
public sealed record SyncCompletePacket(int CanvasSequence) : IPacket;
