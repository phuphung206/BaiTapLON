using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasClient.Canvas;

/// <summary>
/// Một điểm trên nét vẽ, tọa độ CHUẨN HÓA (normalized) trong khoảng [0,1]
/// theo chiều rộng/cao của canvas — KHÔNG dùng pixel tuyệt đối.
/// Lý do: mỗi Client có thể resize cửa sổ khác nhau; nếu lưu pixel tuyệt đối,
/// nét vẽ của người có màn hình 1920px sẽ méo/lệch vị trí khi hiển thị trên
/// máy có canvas 1000px. Quy đổi sang pixel thật chỉ thực hiện lúc render.
/// </summary>
public readonly record struct StrokePoint(float X, float Y);

/// <summary>
/// Đơn vị dữ liệu một nét vẽ hoàn chỉnh. Đây là "nội dung thật" mà Client tự
/// định nghĩa và nhồi vào <c>StrokePacket.StrokeData</c> (byte[] mà Server coi
/// là mù, không đọc). StrokeId + AuthorSessionId được thêm ngay từ đầu để dành
/// chỗ cho tính năng Undo/Redo độc lập theo người dùng ở giai đoạn sau — muốn
/// undo một nét, Client (và Server, khi được mở rộng) cần biết nét đó là của ai.
/// </summary>
public sealed record Stroke(
    Guid StrokeId,
    string AuthorSessionId,
    string ColorHex,        // "#RRGGBB"
    float Thickness,        // đơn vị canvas chuẩn hóa cùng hệ với StrokePoint (xem CollaborativeCanvasControl)
    IReadOnlyList<StrokePoint> Points,
    long CreatedAtUnixMs)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public static Stroke FromBytes(byte[] data)
    {
        return JsonSerializer.Deserialize<Stroke>(data, JsonOptions)
            ?? throw new InvalidDataException("Không thể giải mã dữ liệu nét vẽ (StrokeData rỗng hoặc sai định dạng).");
    }
}
