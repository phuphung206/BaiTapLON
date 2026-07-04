using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CanvasClient.Protocol;

/// <summary>
/// Đóng/mở khung gói tin giống hệt <c>PacketFraming</c> phía Server:
/// [4 byte độ dài, big-endian] + [payload JSON UTF-8]. Hai bên PHẢI
/// đồng nhất tuyệt đối ở đây, nếu không sẽ đọc lệch khung ngay gói đầu tiên.
/// </summary>
public static class PacketFraming
{
    public const int MaxFrameSize = 16 * 1024 * 1024; // 16 MB — khớp giới hạn Server

    public static async Task<IPacket?> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] lenBuffer = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(lenBuffer, ct);
        }
        catch (EndOfStreamException)
        {
            return null; // Server đóng kết nối gọn gàng, giữa hai frame
        }

        int frameLength = BinaryPrimitives.ReadInt32BigEndian(lenBuffer);
        if (frameLength <= 0 || frameLength > MaxFrameSize)
            throw new InvalidDataException($"Kích thước frame không hợp lệ: {frameLength}");

        byte[] payload = new byte[frameLength];
        await stream.ReadExactlyAsync(payload, ct); // EOF ở đây = đứt giữa chừng, để propagate lên

        return JsonSerializer.Deserialize<IPacket>(payload);
    }

    public static async Task WritePacketAsync(NetworkStream stream, IPacket packet, CancellationToken ct)
    {
        // ⚠ Bắt buộc dùng generic argument <IPacket> tường minh — nếu để trình biên
        // dịch suy luận theo kiểu cụ thể (vd StrokePacket) thì "$type" sẽ KHÔNG được
        // ghi ra, phía Server deserialize theo IPacket sẽ không biết nạp vào type nào.
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes<IPacket>(packet);

        if (payload.Length > MaxFrameSize)
            throw new InvalidOperationException("Payload vượt quá giới hạn frame cho phép.");

        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);

        await stream.WriteAsync(frame, ct);
    }
}
