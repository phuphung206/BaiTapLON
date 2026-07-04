# CanvasClient — Canvas renderer (real-time canvas)

Đây là phần **Client WinUI 3** phụ trách canvas cộng tác thời gian thực, tương
thích với giao thức TCP đã có ở Server (`PacketFraming` + `IPacket` trong
`SharedModels.cs`). Phạm vi cố tình giới hạn ở đúng nhóm chức năng "Real-time
Interaction / Shared Canvas 2D" + phần handshake bắt buộc để canvas hoạt động
đúng — **không** bao gồm UDP LAN Discovery, Chat UI, hay bảng điều khiển
Owner (Kick/Ban/Lock). Xem mục "Ngoài phạm vi" bên dưới.

## Cách thêm vào solution hiện có

1. Add Existing Project vào `.sln` hiện tại → chọn `CanvasClient.csproj`.
2. Đây là project **độc lập** (không tham chiếu project Server) — hai bên chỉ
   nói chuyện qua TCP, không dùng chung assembly. Nếu namespace gốc solution
   của bạn khác `CanvasClient`, đổi `RootNamespace` trong `.csproj` cho khớp.
3. Set cả hai project (`Server` và `CanvasClient`) làm Startup Projects để F5
   chạy song song lúc debug.

## Những gì đã build và đã kiểm chứng được

`Protocol/`, `Networking/`, `Canvas/Stroke.cs` là C# thuần (không đụng WinUI),
nên mình đã build **thật** bằng .NET 10 SDK và chạy một bộ test end-to-end
gồm: round-trip JSON polymorphic cho từng loại `IPacket`, round-trip
`Stroke.ToBytes()/FromBytes()`, và một phiên TCP loopback thật (không mock)
mô phỏng đúng trình tự handshake `SyncStart → SnapshotChunk × N → DeltaSync →
SyncComplete` rồi gửi ngược một `StrokePacket` — tất cả pass. Việc này giúp
loại trừ phần lớn rủi ro sai lệch giao thức trước khi bạn build trên Windows.

Phần XAML/Win2D (`CollaborativeCanvasControl`, `MainWindow`, `App`) viết theo
đúng API Win2D/WinUI3 mình nắm được, nhưng **chưa build-verify được** — môi
trường mình chạy là Linux, WinUI 3 chỉ chạy trên Windows và mình cũng không
tải được gói NuGet (`nuget.org` ngoài whitelist mạng của mình) để restore thử.
Build lần đầu trên máy bạn có thể cần sửa vài chỗ nhỏ về tên API.

## Quyết định thiết kế đáng chú ý

- **Tọa độ chuẩn hóa [0,1]**, không dùng pixel tuyệt đối (`Canvas/Stroke.cs`).
  Mỗi Client có thể resize cửa sổ khác nhau; lưu tuyệt đối sẽ làm nét vẽ méo/
  lệch giữa các máy có kích thước canvas khác nhau.
- **`StrokeData` là JSON tự quy ước**, không phải định dạng nhị phân. Server
  coi `byte[]` này là mù (chỉ lưu/relay, không đọc — xem `CanvasState`), nên
  Client toàn quyền định nghĩa nội dung bên trong. Dùng JSON để nhất quán với
  phần còn lại của giao thức và dễ debug (`Console.WriteLine` ra là đọc được
  ngay), đánh đổi lấy việc nặng hơn định dạng nhị phân — nếu sau này canvas có
  hàng chục nghìn nét, đây là chỗ đầu tiên nên tối ưu.
- **`Stroke` có sẵn `StrokeId` + `AuthorSessionId`** dù tính năng Undo/Redo độc
  lập chưa được implement ở đây. Đây là nền để thêm sau mà không phải đổi định
  dạng dữ liệu lần nữa (xem mục "Ngoài phạm vi").
- **Gate UI-side**: `CollaborativeCanvasControl` mặc định khóa vẽ
  (`IsSynced = false`) và chỉ mở khi nhận đủ `SyncCompletePacket`. Lớp phủ
  đồng bộ vừa hiển thị tiến trình vừa tự chặn hit-test pointer — không có
  đường tắt nào để vẽ trước khi đồng bộ xong.
- **Bake tăng dần (incremental bake) thay vì vẽ lại toàn bộ mỗi khung hình**:
  các nét đã chốt được "nướng" một lần vào một `CanvasRenderTarget` (bitmap
  GPU) — thêm một nét mới là O(1), không phải vẽ lại từ đầu lịch sử mỗi lần
  `Invalidate()`. Chỉ nét đang vẽ dở của chính mình mới được vẽ lại mỗi khung
  hình (rẻ, vì chỉ có một nét).
- **Không chờ round-trip khi vẽ cục bộ**: theo `HandleIncomingStrokeAsync`
  phía Server, người gửi **không** nhận lại chính `StrokePacket` của mình
  (server chỉ broadcast cho các member khác). Vì vậy `CommitLocalStroke` vẽ
  lên canvas ngay lập tức, việc gửi lên Server chỉ để đồng bộ cho người khác.
- **`ServerConnection` không biết gì về Canvas** — nó chỉ gửi/nhận `IPacket`
  chung chung qua một `Channel`. `CollaborativeCanvasControl` là một trong
  nhiều consumer có thể có của sự kiện `PacketReceived`; phần Room/Chat có thể
  lắng nghe độc lập trên cùng một `ServerConnection` mà không đụng vào nhau.

## ⚠️ Cần sửa ở Server để hai bên bắt tay được

Trong lúc đọc `SharedModels.cs`/`TcpServerHandler.cs`/`UdpBroadcastHandler.cs`
để dựng đúng hợp đồng dữ liệu, mình thấy vài chỗ hiện KHÔNG compile — nêu ra
để bạn không mất thời gian dò khi build lại Server:

1. **`SharedModels.cs`**: cụm attribute `[JsonPolymorphic]` +
   `[JsonDerivedType]` hiện đang nằm ngay phía trên `public enum
   RoomActionResult` thay vì `public interface IPacket` — attribute này chỉ
   hợp lệ trên class/interface nên sẽ báo lỗi biên dịch. `Protocol/Packets.cs`
   trong project này đã đặt lại đúng chỗ; cần sửa y hệt bên Server (dời cả
   khối lên trên `public interface IPacket { }`) thì JSON `$type` mới ra đúng
   và hai bên mới nói chuyện được.
2. **`TcpServerHandler.cs`**: `SendAsync` và `BroadcastAsync` có
   `return Task.CompletedTask;` bên trong một method khai báo `async Task` —
   đây là lỗi biên dịch (method `async Task` chỉ được `return;` suông). Phần
   code phía sau `return` đó hiện đang là dead code.
3. **`UdpBroadcastHandler.cs`**: bên trong `SendBeaconAsync` có một đoạn code
   rõ ràng thuộc về phía Client (`_discoveryClient.OnDiscoveryUpdated +=`,
   `_dispatcherQueue.TryEnqueue`, `RoomList.Add(...)`) — có vẻ bị dán nhầm
   file lúc soạn thảo. Đoạn này gợi ý khá rõ bạn đã phác thảo sẵn cơ chế lắng
   nghe Discovery ở Client (DispatcherQueue + `ObservableCollection`
   `RoomList`) — nếu muốn, mình có thể dựng tiếp `DiscoveryClient` theo đúng
   pattern đó ở lượt sau.

Mình chưa sửa trực tiếp các file Server (ngoài phạm vi được yêu cầu lần này),
chỉ nêu ra để bạn quyết định — báo mình nếu muốn mình vá luôn.

## Ngoài phạm vi (chưa làm ở đây)

- **UDP LAN Discovery** (mục 3.1) — Client demo trong repo này kết nối bằng
  IP/port nhập tay. `Protocol/DiscoveryPackets.cs` (mirror của
  `IDiscoveryPacket` bên Server) chưa được tạo; cần thêm nếu muốn dò phòng tự
  động qua broadcast port 11001.
- **Chat nhóm, Room browser, Owner/Kick/Ban/Lock UI** (mục 3.2, 3.3) — các
  packet tương ứng (`ChatMessagePacket`, `RoomStateInfoPacket`,
  `KickRequestPacket`...) đã có sẵn trong `Protocol/Packets.cs` nên phần Server
  không cần đổi gì thêm, nhưng UI/ViewModel cho các phần này thì chưa dựng.
- **Undo/Redo độc lập theo người dùng** (mục 3.2) — hiện **không có** trong
  giao thức Server (`CanvasState` chỉ có `AppendStroke`, không có cơ chế xóa/
  tombstone một nét theo `StrokeId`, và không có packet loại
  `UndoStrokeRequest`/`StrokeRemoved`). `Stroke` ở Client đã có `StrokeId` +
  `AuthorSessionId` sẵn sàng cho việc này, nhưng Undo/Redo *đồng bộ giữa các
  Client* cần mở rộng giao thức + `CanvasState` phía Server trước — đây nên là
  một hạng mục riêng, không lồng vào phần canvas renderer này.

## Chạy thử nhanh

1. Sửa + build lại Server theo mục "Cần sửa ở Server" ở trên, chạy Server.
2. Build `CanvasClient`, chạy **hai instance** (Debug → chạy thêm một instance
   thứ hai, hoặc mở `.exe` trong `bin/.../` hai lần).
3. Ở cả hai cửa sổ: nhập đúng IP/port của Server → bấm "Kết nối & tạo phòng"
   (instance thứ hai nên đổi `CreateRoomRequestPacket` demo sang
   `JoinRoomRequestPacket` với đúng `RoomId` nếu muốn vào CHUNG phòng — bản
   demo `MainWindow` hiện chỉ có nút Create để giữ UI tối giản, xem
   `MainWindow.xaml.cs`).
4. Vẽ ở một cửa sổ → nét vẽ phải xuất hiện gần như tức thời ở cửa sổ còn lại.
