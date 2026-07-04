# CanvasServer — Nhật ký thay đổi

Toàn bộ đã build thật (`dotnet build`, .NET 10 SDK) và chạy thật qua `test_client.py`
(client giả lập nói đúng protocol khung 4-byte length + JSON) — không chỉ đọc code bằng mắt.

## 1. Lỗi biên dịch đã sửa (trước đây project không build được)

| File | Vấn đề |
|---|---|
| `SynCoordinator.cs` | Không có `using`/`namespace` nào cả — mọi type đều "not found". |
| `ChatHandler.cs` / `SharedModels.cs` | `ChatHandler` thật nằm lẫn trong `SharedModels.cs` (namespace `Models`), còn file `ChatHandler.cs` bị comment 100%. `Program.cs` không `using` namespace đó nên không resolve được. → Đã tách `ChatHandler` ra đúng file của nó, namespace `CanvasServer.Core`. |
| `Room.cs` | `SyncCoordinator` gọi `room.GetMemberInfos()` nhưng method này chưa từng được định nghĩa (CS1061). Đã thêm. |
| `Room.cs` | `ChatHandler.HandleIncomingAsync` gọi `room.BroadcastAsync(...)` — method không tồn tại trên `Room`. Đã đổi sang dùng `IPacketSender` (đã inject sẵn) thay vì method ảo trên `Room`. |
| `DiscoveryPackets.cs` | Thiếu `using CanvasServer.Models;` cho `RoomInfo`. |
| `SharedModels.cs` | `[JsonPolymorphic]`/`[JsonDerivedType]` bị gắn nhầm lên `enum RoomActionResult` (CS0592 — 2 attribute này chỉ hợp lệ trên class/interface). Đã chuyển về đúng chỗ: gắn thẳng lên `interface IPacket`. |
| `TcpServerHandler.cs` | `WriterLoopAsync`/`HeartbeatLoopAsync` bị viết nhầm bên trong `static class PacketFraming` (static class không được chứa instance member — CS0708), trong khi `TcpServerHandler` lại gọi 2 hàm này như thể là của chính nó. Đã chuyển 2 hàm về đúng `TcpServerHandler`. |
| `TcpServerHandler.cs` | `SendAsync`/`BroadcastAsync` là `async Task` nhưng có `return Task.CompletedTask;` (CS1997 — `async Task` không được return giá trị) + một đống dead code phía sau không bao giờ chạy tới. Đã xoá hẳn 2 method trùng lặp này, dùng thống nhất `IPacketSender` (vốn đã được inject nhưng gần như không được dùng). |
| `UdpBroadcastHandler.cs` | Giữa `SendBeaconAsync` có nguyên một đoạn code Client (WinUI `DispatcherQueue`, `_discoveryClient`, `RoomList`...) bị dán nhầm vào — toàn field/class không tồn tại ở Server. Đã xoá. |

## 2. Tính năng mới (theo yêu cầu)

**Ban** — `_bannedIps` trước đây chỉ được *đọc* trong `TryJoin`, chưa từng có chỗ nào *ghi* vào — Ban coi như tính năng chết dù đã có "bộ xương". Đã thêm `Room.TryBan`, packet `BanRequestPacket`, và `TcpServerHandler.HandleBanAsync` (mirror cấu trúc của Kick).

**Undo/Redo độc lập theo từng người** — trước đây hoàn toàn chưa tồn tại: `CanvasState` chỉ là 1 `List<byte[]>` dùng chung, không có khái niệm "nét này của ai". Đã thiết kế lại:
- Mỗi stroke giờ có `OwnerSessionId` + cờ `IsActive` (không bao giờ xoá khỏi list gốc — Undo/Redo chỉ bật/tắt cờ, giữ nguyên z-order khi vẽ lại).
- Mỗi user có 1 ngăn xếp Redo riêng (LIFO), độc lập hoàn toàn với người khác.
- Vẽ nét mới sẽ xoá sạch nhánh Redo cũ của **chính người đó** (đúng chuẩn hành vi editor).
- Packet mới: `UndoRequestPacket`, `RedoRequestPacket` (client→server, không cần field), `StrokeUndonePacket`, `StrokeRedonePacket` (server→client, broadcast).
- Server luôn tự xác định "nét gần nhất của ai" dựa vào session đang gửi gói tin — **không tin** bất kỳ `OwnerSessionId` nào client tự gửi lên (đã test: client cố tình gửi `OwnerSessionId` giả trong `StrokePacket`, server bỏ qua và tự gán đúng người gửi thật).

**2 khoảng hở nhỏ phát hiện thêm khi đối chiếu với đề tài** (đã làm luôn vì rất nhỏ, cùng pattern với Ban):
- **Khoá/mở phòng**: `RoomLockedPacket` + `SystemMessages.RoomLocked` đã có sẵn nhưng chưa từng có đường nào set `_isLocked = true` — khoá phòng trước đây là tính năng chết. Thêm `Room.TrySetLocked` + `SetRoomLockRequestPacket`.
- **Đổi mật khẩu phòng sau khi tạo**: tương tự, thêm `Room.TryChangePassword` + `ChangePasswordRequestPacket`.

## 3. Bug runtime bắt được nhờ test thật (không thấy được nếu chỉ đọc code / chỉ build)

1. **Race condition khi Kick/Ban**: `SendAsync(...)` chỉ *enqueue* gói tin (fire-and-forget) rồi dòng kế tiếp gọi `Connection.Close()` ngay lập tức — WriterLoop chưa chắc kịp flush gói `YouWereKicked/BannedPacket` ra socket trước khi socket bị đóng. Test bắt được lỗi `Cannot access a disposed object` — client bị kick/ban có thể không bao giờ biết lý do. Đã sửa bằng `SendThenCloseAsync`: ghi trực tiếp gói tin (có khoá `WriteLock` dùng chung với WriterLoop để không xé khung dữ liệu) rồi mới đóng kết nối.
2. **Thông báo "đã rời phòng" chồng chéo sau Kick/Ban**: đóng `Connection` sau khi Kick/Ban khiến vòng đọc của chính nạn nhân ném exception → tự động chạy `HandleLeaveOrDisconnectAsync` lần nữa → bắn thêm "X đã rời phòng" dù vừa mới gửi "X đã bị kick/ban" — mâu thuẫn nhau. Đã sửa: `HandleLeaveOrDisconnectAsync` bỏ qua nếu `TryLeave` trả về `TargetNotFound` (nghĩa là đã bị Kick/Ban xử lý từ trước).
3. (Nhân tiện) `MemberLeftPacket` từng bị broadcast 2 lần liên tiếp cho 1 lần rời phòng bình thường — đã gộp còn 1 lần.
4. `_port` field để log khởi động bị hardcode `11000`, lệch với `config.TcpPort` thật sự dùng để bind — sửa để log luôn đúng port thật.

## 4. Đổi protocol — client WinUI cần cập nhật theo

- `StrokePacket` thêm field `OwnerSessionId` (bắt buộc, để hỗ trợ Undo/Redo theo người).
- `SnapshotChunkPacket`: field `ChunkData` (`byte[]` trần) đổi thành `Stroke` (bọc nguyên 1 `StrokePacket`, gồm cả Sequence + Owner).
- `DeltaSyncPacket`: field đổi tên `MissedStrokes` (`IReadOnlyList<StrokePacket>`) → `MissedEvents` (`IReadOnlyList<IPacket>`), vì giờ có thể chứa cả Undo/Redo, không chỉ stroke mới.
- Packet mới cần client xử lý: `BanRequestPacket`, `UndoRequestPacket`, `RedoRequestPacket`, `StrokeUndonePacket`, `StrokeRedonePacket`, `SetRoomLockRequestPacket`, `ChangePasswordRequestPacket`.
- Toàn bộ `RoomActionResult`/`ChatMessageType` vẫn serialize dạng số nguyên (không có `JsonStringEnumConverter`) — giữ nguyên hành vi gốc, client cần tự map số → tên nếu muốn hiển thị.

## 5. Cách tự kiểm tra lại

```
dotnet build
dotnet bin/Debug/net10.0/CanvasServer.dll   # chạy server, mặc định cổng 11000/11001
python3 test_client.py                       # chạy đủ 11 kịch bản, in "ALL CHECKS PASSED" nếu ổn
```

`test_client.py` không cần cài gì thêm ngoài Python 3 chuẩn (chỉ dùng `socket`/`json`/`struct`) — tự nói đúng
protocol khung 4-byte length + JSON, mô phỏng nhiều client cùng lúc: tạo phòng, join, vẽ, Undo/Redo, Lock,
đổi mật khẩu, Ban, rồi kiểm tra người vừa join sau cùng nhận đúng snapshot đã phản ánh Undo/Redo.
