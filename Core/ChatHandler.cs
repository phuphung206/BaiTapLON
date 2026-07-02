using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CanvasServer.Models;

namespace CanvasServer.Core;

public sealed class ChatHandler
{
    private const int MaxContentLength = 500;
    private readonly IPacketSender   _sender;
    private readonly ChatRateLimiter _rateLimiter = new();

    public ChatHandler(IPacketSender sender) => _sender = sender;

    //══════════════════════════════════════════════════════════════════
    //  XỬ LÝ TIN NHẮN TỪ CLIENT
    //══════════════════════════════════════════════════════════════════
    public async Task HandleIncomingAsync(ClientSession sender, Room room, SendChatMessagePacket req, CancellationToken ct)
    {
        // ── Validate ─────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(req.Content)) return;
        var content = Sanitize(req.Content.Trim());
        if (content.Length == 0) return;
        if (content.Length > MaxContentLength)
        {
            await SendErrorAsync(sender, $"Tin nhắn quá dài (tối đa {MaxContentLength} ký tự).", ct);
            return;
        }
        // ── Rate limit ───────────────────────────────────────────────
        if (!_rateLimiter.TryConsume(sender.SessionId))
        {
            await SendErrorAsync(sender, "Gửi quá nhanh, vui lòng chờ vài giây.", ct);
            return;
        }
        // ── Store + broadcast ─────────────────────────────────────────
        var msg = room.Chat.AddUserMessage(sender.SessionId, sender.DisplayName, content);
        await _sender.BroadcastAsync(room, new ChatMessagePacket(msg), excludeId: null, ct);
    }

    //══════════════════════════════════════════════════════════════════
    //  SYSTEM MESSAGES — gọi từ các handler khác
    //══════════════════════════════════════════════════════════════════
    public async Task BroadcastSystemAsync(Room room, string text, CancellationToken ct)
    {
        var msg = room.Chat.AddSystemMessage(text);
        await _sender.BroadcastAsync(room, new ChatMessagePacket(msg), excludeId: null, ct);
    }

    //══════════════════════════════════════════════════════════════════
    //  GỬI HISTORY CHO CLIENT MỚI (gọi từ SyncCoordinator)
    //══════════════════════════════════════════════════════════════════
    public async Task SendHistoryAsync(ClientSession client, Room room, CancellationToken ct)
    {
        var history = room.Chat.GetSnapshot();
        if (history.Count > 0)
            await _sender.SendAsync(client, new ChatHistoryPacket(history), ct);
    }

    //══════════════════════════════════════════════════════════════════
    //  CLEANUP khi session kết thúc
    //══════════════════════════════════════════════════════════════════
    public void OnSessionEnded(string sessionId)
        => _rateLimiter.Remove(sessionId);

    //══════════════════════════════════════════════════════════════════
    //  SANITIZE
    //  Loại bỏ control chars, giữ lại newline và tab để hỗ trợ
    //  multiline message nếu cần.
    //══════════════════════════════════════════════════════════════════
    private static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
            if (!char.IsControl(c) || c is '\n' or '\t')
                sb.Append(c);
        return sb.ToString().Trim();
    }

    private Task SendErrorAsync(ClientSession s, string msg, CancellationToken ct)
        => _sender.SendAsync(s, new ErrorPacket(msg), ct);
}
