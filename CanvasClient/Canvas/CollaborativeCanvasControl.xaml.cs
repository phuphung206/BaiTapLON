using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CanvasClient.Networking;
using CanvasClient.Protocol;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI;
using Windows.UI;

namespace CanvasClient.Canvas;

/// <summary>
/// Control canvas cộng tác thời gian thực. Trách nhiệm DUY NHẤT ở đây là canvas:
/// bắt input vẽ cục bộ, gửi lên Server, áp nét vẽ tới từ người khác, và chạy đúng
/// handshake đồng bộ (SyncStart -> SnapshotChunk* -> DeltaSync -> SyncComplete).
/// Room/Chat/Owner/Kick... KHÔNG thuộc phạm vi control này — trang/ViewModel chứa
/// nó có thể tự lắng nghe thêm ServerConnection.PacketReceived cho các gói đó,
/// độc lập với những gì diễn ra ở đây.
/// </summary>
public sealed partial class CollaborativeCanvasControl : UserControl
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    // Nguồn sự thật duy nhất cho nội dung canvas. _committedInk chỉ là cache render
    // của danh sách này — luôn có thể build lại từ đầu từ _strokes bất kỳ lúc nào.
    private readonly List<Stroke> _strokes = [];
    private readonly HashSet<Guid> _bakedStrokeIds = [];

    // Nét đang vẽ dở (chưa nhả chuột/bút) — chỉ tồn tại cục bộ, chưa gửi lên Server.
    private readonly List<StrokePoint> _activePoints = [];
    private bool _isDrawing;

    private CanvasRenderTarget? _committedInk;
    private ServerConnection?   _connection;

    /// <summary>Nổ ra mỗi khi trạng thái đồng bộ đổi — giá trị = IsSynced mới.</summary>
    public event EventHandler<bool>? SyncStateChanged;

    public bool IsSynced { get; private set; }

    /// <summary>SessionId Server gán cho chính mình. PHẢI gán giá trị này (lấy từ
    /// RoomStateInfoPacket.YourSessionId) trước khi người dùng bắt đầu vẽ, để nét vẽ
    /// gửi đi mang đúng AuthorSessionId — cần cho Undo/Redo độc lập theo người dùng sau này.</summary>
    public string LocalSessionId { get; set; } = "";

    public Color  StrokeColor     { get; set; } = Colors.Black;
    public double StrokeThickness { get; set; } = 4.0;

    public CollaborativeCanvasControl()
    {
        this.InitializeComponent();
        SetSyncing(true);
    }

    // ─────────────────────────────────────────────────────────────
    //  Gắn / gỡ kết nối
    // ─────────────────────────────────────────────────────────────
    public void AttachConnection(ServerConnection connection)
    {
        DetachConnection();
        _connection = connection;
        _connection.PacketReceived += OnPacketReceived;
        SetSyncing(true);
        SyncStatusText.Text = "Đã kết nối — đang chờ vào phòng...";
    }

    public void DetachConnection()
    {
        if (_connection is null) return;
        _connection.PacketReceived -= OnPacketReceived;
        _connection = null;
    }

    /// <summary>Gọi trước khi gửi JoinRoomRequest/CreateRoomRequest — kể cả lần đầu tiên —
    /// để đưa control về trạng thái sạch, khóa vẽ lại chờ handshake của phòng (mới/khác).</summary>
    public void ResetForNewRoom()
    {
        _strokes.Clear();
        _bakedStrokeIds.Clear();
        _activePoints.Clear();
        _isDrawing = false;
        SetSyncing(true);
        SyncStatusText.Text = "Đang chờ dữ liệu phòng...";
        _ = RebuildCommittedInkAsync();
    }

    // ─────────────────────────────────────────────────────────────
    //  Nhận gói tin mạng — CHẠY TRÊN THREAD NỀN (thread đọc socket của
    //  ServerConnection). Việc đầu tiên luôn là marshal về UI thread;
    //  từ HandleCanvasPacket trở đi, mọi thứ chạy trên UI thread.
    // ─────────────────────────────────────────────────────────────
    private void OnPacketReceived(IPacket packet)
    {
        if (packet is not (SyncStartPacket or SnapshotChunkPacket or DeltaSyncPacket or SyncCompletePacket or StrokePacket))
            return; // không thuộc phạm vi Canvas — để tầng khác (Room/Chat) tự xử lý

        _dispatcherQueue.TryEnqueue(() => HandleCanvasPacket(packet));
    }

    private void HandleCanvasPacket(IPacket packet)
    {
        switch (packet)
        {
            case SyncStartPacket:
                SetSyncing(true);
                SyncStatusText.Text = "Đang tải dữ liệu bảng vẽ...";
                break;

            case SnapshotChunkPacket p:
                ApplyIncomingStroke(p.ChunkData);
                SyncStatusText.Text = $"Đã tải {_strokes.Count} nét vẽ...";
                break;

            case DeltaSyncPacket p:
                foreach (var missed in p.MissedStrokes)
                    ApplyIncomingStroke(missed.StrokeData);
                break;

            case SyncCompletePacket:
                SetSyncing(false);
                break;

            case StrokePacket p:
                // Theo Server, gói loại này chỉ tới SAU khi client đã hết IsSyncing
                // (xem HandleIncomingStrokeAsync phía Server) — xử lý ở đây chỉ để
                // phòng thủ, không phải luồng chính.
                ApplyIncomingStroke(p.StrokeData);
                break;
        }
    }

    private void ApplyIncomingStroke(byte[] strokeData)
    {
        Stroke stroke;
        try { stroke = Stroke.FromBytes(strokeData); }
        catch (Exception)
        {
            // StrokeData hỏng / sai định dạng — bỏ qua đúng MỘT nét, không để cả
            // phiên đồng bộ sập vì một gói lỗi.
            return;
        }
        AddAndBakeStroke(stroke);
    }

    // ─────────────────────────────────────────────────────────────
    //  Trạng thái đồng bộ / gate UI
    // ─────────────────────────────────────────────────────────────
    private void SetSyncing(bool syncing)
    {
        IsSynced = !syncing;
        SyncOverlay.Visibility = syncing ? Visibility.Visible : Visibility.Collapsed;
        SyncStateChanged?.Invoke(this, IsSynced);
    }

    // ─────────────────────────────────────────────────────────────
    //  Vẽ cục bộ — bắt Pointer, chuẩn hóa tọa độ, gửi lên Server
    // ─────────────────────────────────────────────────────────────
    private void DrawSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSynced) return; // chốt gate lần 2 — SyncOverlay đã chặn hit-test rồi, đây là phòng thủ

        var point = e.GetCurrentPoint(DrawSurface);
        if (!point.IsInContact) return;

        _isDrawing = true;
        _activePoints.Clear();
        _activePoints.Add(ToNormalized(point.Position));
        DrawSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void DrawSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawing) return;

        // GetIntermediatePoints lấy TẤT CẢ điểm kể từ sự kiện trước, không chỉ điểm
        // mới nhất — chuột/bút di chuyển nhanh giữa hai khung hình vẫn không bị rớt điểm.
        foreach (var ip in e.GetIntermediatePoints(DrawSurface))
        {
            if (ip.IsInContact)
                _activePoints.Add(ToNormalized(ip.Position));
        }
        DrawSurface.Invalidate();
        e.Handled = true;
    }

    private void DrawSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawing) return;
        _isDrawing = false;
        DrawSurface.ReleasePointerCapture(e.Pointer);
        CommitLocalStroke();
        e.Handled = true;
    }

    private void DrawSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Kéo ra khỏi biên control mà vẫn giữ nút chuột: chốt nét ngay tại đây thay
        // vì làm mất luôn phần đang vẽ dở.
        if (_isDrawing)
        {
            _isDrawing = false;
            CommitLocalStroke();
        }
    }

    private void DrawSurface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isDrawing = false;
        _activePoints.Clear();
        DrawSurface.Invalidate();
    }

    private void CommitLocalStroke()
    {
        if (_activePoints.Count < 2)
        {
            _activePoints.Clear();
            DrawSurface.Invalidate();
            return;
        }

        var stroke = new Stroke(
            StrokeId:        Guid.NewGuid(),
            AuthorSessionId: LocalSessionId,
            ColorHex:        ColorToHex(StrokeColor),
            Thickness:       (float)StrokeThickness,
            Points:          [.. _activePoints],
            CreatedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _activePoints.Clear();

        AddAndBakeStroke(stroke); // vẽ cục bộ NGAY LẬP TỨC — không chờ round-trip

        // Sequence=0: Server tự gán số thứ tự thật trong CanvasState.AppendStroke,
        // giá trị Client gửi lên không được Server đọc (xem HandleIncomingStrokeAsync).
        // Server cũng KHÔNG echo gói này lại cho chính người gửi — vẽ cục bộ ở trên
        // là bắt buộc, không phải tối ưu.
        _connection?.Send(new StrokePacket(0, stroke.ToBytes()));
    }

    // ─────────────────────────────────────────────────────────────
    //  Bake nét vẽ vào bitmap cache (Win2D) + yêu cầu vẽ lại khung hình
    // ─────────────────────────────────────────────────────────────
    private void AddAndBakeStroke(Stroke stroke)
    {
        if (!_bakedStrokeIds.Add(stroke.StrokeId)) return; // trùng lặp (vd. DeltaSync gửi lại) — bỏ qua, idempotent

        _strokes.Add(stroke);

        if (_committedInk is not null)
        {
            // Bake ĐÚNG MỘT nét mới vào bitmap đã có, O(1) theo số nét — không vẽ lại
            // toàn bộ lịch sử mỗi lần có nét mới. Vì bitmap này được tạo với kích thước
            // cố định (xem RebuildCommittedInkAsync), chuẩn hóa toạ độ dùng chính kích
            // thước ĐÓ, không dùng ActualWidth/Height hiện tại của control (có thể đã
            // đổi nếu người dùng vừa resize cửa sổ nhưng canvas chưa kịp rebuild).
            using var ds = _committedInk.CreateDrawingSession();
            DrawStrokeToSession(ds, stroke, _committedInk.Size.Width, _committedInk.Size.Height);
        }

        DrawSurface.Invalidate();
    }

    private async Task RebuildCommittedInkAsync()
    {
        if (!DrawSurface.ReadyToDraw) return; // thiết bị Win2D chưa sẵn sàng (chưa CreateResources lần nào)

        double w = Math.Max(1, DrawSurface.ActualWidth);
        double h = Math.Max(1, DrawSurface.ActualHeight);

        var newTarget = new CanvasRenderTarget(DrawSurface, (float)w, (float)h);
        using (var ds = newTarget.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            int i = 0;
            foreach (var stroke in _strokes)
            {
                DrawStrokeToSession(ds, stroke, w, h);
                if (++i % 50 == 0) await Task.Yield(); // nhiều nét — nhường thread, không đứng UI lúc resize/tải lại
            }
        }

        _committedInk?.Dispose();
        _committedInk = newTarget;
        DrawSurface.Invalidate();
    }

    private void DrawSurface_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
        => args.TrackAsyncAction(RebuildCommittedInkAsync().AsAsyncAction());

    private async void DrawSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try { await RebuildCommittedInkAsync(); }
        catch (Exception)
        {
            // Có thể rơi vào giữa lúc thiết bị đồ họa chưa sẵn sàng ngay sau resize đầu
            // tiên — CreateResources sẽ tự chạy lại khi thiết bị sẵn sàng, không cần retry ở đây.
        }
    }

    private void DrawSurface_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(Colors.White);

        if (_committedInk is not null)
            ds.DrawImage(_committedInk);

        // Nét đang vẽ dở của CHÍNH MÌNH — vẽ trực tiếp mỗi khung hình, không bake vào
        // cache (chỉ 1 nét, rẻ) — bake sẽ diễn ra một lần duy nhất lúc CommitLocalStroke.
        if (_isDrawing && _activePoints.Count > 1)
        {
            var live = new Stroke(Guid.Empty, LocalSessionId, ColorToHex(StrokeColor), (float)StrokeThickness, _activePoints, 0);
            DrawStrokeToSession(ds, live, DrawSurface.ActualWidth, DrawSurface.ActualHeight);
        }
    }

    private static void DrawStrokeToSession(CanvasDrawingSession ds, Stroke stroke, double width, double height)
    {
        if (stroke.Points.Count == 0 || width <= 0 || height <= 0) return;

        var color = HexToColor(stroke.ColorHex);

        if (stroke.Points.Count == 1)
        {
            // Một cú chạm không rê chuột (chấm tròn) — vẫn phải hiển thị được gì đó.
            ds.FillCircle(ToAbsolute(stroke.Points[0], width, height), stroke.Thickness / 2f, color);
            return;
        }

        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(ToAbsolute(stroke.Points[0], width, height));
        for (int i = 1; i < stroke.Points.Count; i++)
            builder.AddLine(ToAbsolute(stroke.Points[i], width, height));
        builder.EndFigure(CanvasFigureLoop.Open);

        using var geometry = CanvasGeometry.CreatePath(builder);
        var style = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap   = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round,
        };
        ds.DrawGeometry(geometry, color, stroke.Thickness, style);
    }

    // ─────────────────────────────────────────────────────────────
    //  Tiện ích tọa độ / màu
    // ─────────────────────────────────────────────────────────────
    private StrokePoint ToNormalized(Windows.Foundation.Point p)
    {
        double w = Math.Max(1, DrawSurface.ActualWidth);
        double h = Math.Max(1, DrawSurface.ActualHeight);
        return new StrokePoint((float)(p.X / w), (float)(p.Y / h));
    }

    private static Vector2 ToAbsolute(StrokePoint p, double width, double height)
        => new((float)(p.X * width), (float)(p.Y * height));

    private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color HexToColor(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex[..2], 16);
        byte g = Convert.ToByte(hex[2..4], 16);
        byte b = Convert.ToByte(hex[4..6], 16);
        return Color.FromArgb(255, r, g, b);
    }
}
