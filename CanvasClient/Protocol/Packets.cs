using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CanvasClient.Protocol;

// ─────────────────────────────────────────────────────────────────
//  Bản sao (mirror) 1-1 của SharedModels.cs bên Server.
//  Đây LÀ hợp đồng dữ liệu (wire contract) — tên property, kiểu dữ
//  liệu và discriminator "$type" phải khớp tuyệt đối với Server vì
//  hai bên serialize/deserialize qua System.Text.Json polymorphic.
//
//  Lưu ý: bản gốc SharedModels.cs hiện KHÔNG compile được vì các
//  attribute [JsonPolymorphic]/[JsonDerivedType] đang nằm phía trên
//  "public enum RoomActionResult" thay vì "public interface IPacket"
//  (attribute áp cho enum là lỗi biên dịch CS-series). Ở đây mình đặt
//  lại đúng vị trí — phía Server cũng cần sửa y hệt thì hai bên mới
//  bắt tay được. Toàn bộ discriminator string ("stroke", "sync_start"...)
//  giữ nguyên như dự định ban đầu.
// ─────────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(JoinRoomRequestPacket),   "join_room_request")]
[JsonDerivedType(typeof(CreateRoomRequestPacket), "create_room_request")]
[JsonDerivedType(typeof(JoinRoomRejectedPacket),  "join_room_rejected")]
[JsonDerivedType(typeof(RoomStateInfoPacket),     "room_state_info")]
[JsonDerivedType(typeof(SyncCompletePacket),      "sync_complete")]
[JsonDerivedType(typeof(KickRequestPacket),       "kick_request")]
[JsonDerivedType(typeof(ErrorPacket),             "error")]
[JsonDerivedType(typeof(YouWereKickedPacket),     "you_were_kicked")]
[JsonDerivedType(typeof(YouWereBannedPacket),     "you_were_banned")]
[JsonDerivedType(typeof(MemberKickedPacket),      "member_kicked")]
[JsonDerivedType(typeof(MemberBannedPacket),      "member_banned")]
[JsonDerivedType(typeof(MemberLeftPacket),        "member_left")]
[JsonDerivedType(typeof(OwnerChangedPacket),      "owner_changed")]
[JsonDerivedType(typeof(RoomLockedPacket),        "room_locked")]
[JsonDerivedType(typeof(RoomPasswordChanged),     "room_password_changed")]
[JsonDerivedType(typeof(StrokePacket),            "stroke")]
[JsonDerivedType(typeof(SyncStartPacket),         "sync_start")]
[JsonDerivedType(typeof(SnapshotChunkPacket),     "snapshot_chunk")]
[JsonDerivedType(typeof(DeltaSyncPacket),         "delta_sync")]
[JsonDerivedType(typeof(MemberJoinedPacket),      "member_joined")]
[JsonDerivedType(typeof(SendChatMessagePacket),   "send_chat")]
[JsonDerivedType(typeof(ChatMessagePacket),       "chat_message")]
[JsonDerivedType(typeof(ChatHistoryPacket),       "chat_history")]
[JsonDerivedType(typeof(PingPacket),              "ping")]
[JsonDerivedType(typeof(PongPacket),              "pong")]
public interface IPacket { }

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

// ──── Heartbeat ─────────────────────────────────────────────────
public sealed record PingPacket(long Timestamp) : IPacket;
public sealed record PongPacket(long Timestamp) : IPacket;

// ──── Quản lý phòng ─────────────────────────────────────────────
public sealed record KickRequestPacket(string TargetSessionId) : IPacket;
public sealed record ErrorPacket(string Message) : IPacket;
public sealed record YouWereKickedPacket(string Reason) : IPacket;
public sealed record YouWereBannedPacket(string Reason) : IPacket;
public sealed record MemberKickedPacket(string SessionId, string Name) : IPacket;
public sealed record MemberBannedPacket(string SessionId, string Name) : IPacket;
public sealed record MemberLeftPacket(string SessionId, string Name) : IPacket;
public sealed record OwnerChangedPacket(string NewOwnerSessionId, string NewOwnerName) : IPacket;
public sealed record RoomLockedPacket(bool IsLocked) : IPacket;
public sealed record RoomPasswordChanged(bool HasPassword) : IPacket;

public sealed record JoinRoomRequestPacket(string RoomId, string DisplayName, string? Password) : IPacket;
public sealed record CreateRoomRequestPacket(string RoomName, string DisplayName, string? Password) : IPacket;
public sealed record JoinRoomRejectedPacket(RoomActionResult Reason) : IPacket;

public sealed record RoomStateInfoPacket(
    string RoomId,
    string RoomName,
    bool   HasPassword,
    bool   IsLocked,
    string YourSessionId,
    IReadOnlyList<MemberInfo> Members) : IPacket;

// ──── Canvas: nét vẽ + handshake đồng bộ ────────────────────────
// StrokeData là byte[] "mù" đối với Server — Server chỉ lưu/relay,
// không đọc nội dung. Định dạng bên trong (Canvas/Stroke.cs) do
// Client tự quy ước ở cả hai đầu.
public sealed record StrokePacket(int Sequence, byte[] StrokeData) : IPacket;
public sealed record SyncStartPacket(int SnapshotSequence) : IPacket;
public sealed record SnapshotChunkPacket(byte[] ChunkData, bool IsLastChunk) : IPacket;
public sealed record DeltaSyncPacket(IReadOnlyList<StrokePacket> MissedStrokes) : IPacket;
public sealed record SyncCompletePacket(int CanvasSequence) : IPacket;
public sealed record MemberJoinedPacket(string SessionId, string DisplayName) : IPacket;

// ──── Chat ───────────────────────────────────────────────────────
public enum ChatMessageType { Text, System }

public sealed record ChatMessage(
    string          MessageId,
    string          SenderId,
    string          SenderName,
    string          Content,
    DateTime        SentAt,
    ChatMessageType Type);

public sealed record SendChatMessagePacket(string Content) : IPacket;
public sealed record ChatMessagePacket(ChatMessage Message) : IPacket;
public sealed record ChatHistoryPacket(IReadOnlyList<ChatMessage> Messages) : IPacket;
