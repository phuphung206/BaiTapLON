using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CanvasServer.Models;

namespace CanvasServer.Core;

public sealed class Room : IDisposable
{
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly Dictionary<string, ClientSession> _members = [];
    private readonly HashSet<string> _bannedIps = [];

    private string?  _passwordHash;
    private volatile bool _isLocked = false;         
    private string   _ownerSessionId;

    public ChatHistoryManager Chat { get; } = new();
    public string   Id        { get; } = Guid.NewGuid().ToString("N")[..8].ToUpper();
    public string   Name      { get; }
    public CanvasState Canvas { get; } = new();
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public bool   HasPassword     => _passwordHash is not null;
    public bool   IsLocked        => _isLocked;
    public string OwnerSessionId  => _ownerSessionId;
    public bool   IsEmpty         { get { _rwLock.EnterReadLock(); try { return _members.Count == 0; } finally { _rwLock.ExitReadLock(); } } }

    public Room(string name, ClientSession creator, string? password = null)
    {
        Name = name;
        _ownerSessionId = creator.SessionId;
        creator.IsSyncing = true; 
        _members[creator.SessionId] = creator;
        if (password is not null) _passwordHash = HashPassword(password);
    }

    public RoomActionResult TryJoin(ClientSession client, string? password)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (_isLocked) return RoomActionResult.RoomLocked;
            if (_bannedIps.Contains(client.IpAddress)) return RoomActionResult.AlreadyBanned;
            if (_passwordHash is not null && !VerifyPassword(password, _passwordHash)) return RoomActionResult.WrongPassword;

            client.IsSyncing = true;
            
            _members[client.SessionId] = client;
            return RoomActionResult.Success;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public (RoomActionResult Result, MemberInfo? NewOwner) TryLeave(string sessionId)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (!_members.Remove(sessionId)) return (RoomActionResult.TargetNotFound, null);
            MemberInfo? newOwner = null;
            if (sessionId == _ownerSessionId && _members.Count > 0)
            {
                _ownerSessionId = _members.Keys.First();
                newOwner = _members[_ownerSessionId].ToInfo(isOwner: true);
            }
            return (RoomActionResult.Success, newOwner);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public (RoomActionResult Result, ClientSession? Target) TryKick(string requestorId, string targetId)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (requestorId != _ownerSessionId) return (RoomActionResult.Unauthorized, null);
            if (targetId == _ownerSessionId) return (RoomActionResult.InvalidTarget, null); 
            if (!_members.TryGetValue(targetId, out var target)) return (RoomActionResult.TargetNotFound, null);

            _members.Remove(targetId);
            return (RoomActionResult.Success, target);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    // Ban = Kick + chặn IP quay lại. Trước đây _bannedIps chỉ được ĐỌC trong TryJoin,
    // chưa từng có chỗ nào GHI vào nó — Ban coi như chưa hoạt động dù đã có "bộ xương".
    public (RoomActionResult Result, ClientSession? Target) TryBan(string requestorId, string targetId)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (requestorId != _ownerSessionId) return (RoomActionResult.Unauthorized, null);
            if (targetId == _ownerSessionId) return (RoomActionResult.InvalidTarget, null);
            if (!_members.TryGetValue(targetId, out var target)) return (RoomActionResult.TargetNotFound, null);

            _bannedIps.Add(target.IpAddress);
            _members.Remove(targetId);
            return (RoomActionResult.Success, target);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    // Bonus nhỏ: RoomLockedPacket/SystemMessages.RoomLocked đã tồn tại sẵn nhưng chưa có
    // đường nào để thực sự set _isLocked = true — khoá phòng trước đây là tính năng chết.
    public RoomActionResult TrySetLocked(string requestorId, bool isLocked)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (requestorId != _ownerSessionId) return RoomActionResult.Unauthorized;
            _isLocked = isLocked;
            return RoomActionResult.Success;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    // Bonus nhỏ: tương tự — đổi mật khẩu phòng sau khi tạo trước đây cũng chưa có đường vào.
    public RoomActionResult TryChangePassword(string requestorId, string? newPassword)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (requestorId != _ownerSessionId) return RoomActionResult.Unauthorized;
            _passwordHash = string.IsNullOrEmpty(newPassword) ? null : HashPassword(newPassword);
            return RoomActionResult.Success;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public IReadOnlyList<ClientSession> GetAllMembers()
    {
        _rwLock.EnterReadLock();
        try { return [.._members.Values]; }
        finally { _rwLock.ExitReadLock(); }
    }

    // SyncCoordinator gọi hàm này để dựng RoomStateInfoPacket, nhưng bản gốc chưa từng định nghĩa nó — lỗi biên dịch CS1061.
    public IReadOnlyList<MemberInfo> GetMemberInfos()
    {
        _rwLock.EnterReadLock();
        try { return [.._members.Values.Select(m => m.ToInfo(isOwner: m.SessionId == _ownerSessionId))]; }
        finally { _rwLock.ExitReadLock(); }
    }

    public RoomInfo ToInfo()
    {
        _rwLock.EnterReadLock();
        try
        {
            var ownerName = _members.TryGetValue(_ownerSessionId, out var o) ? o.DisplayName : "?";
            return new(Id, Name, HasPassword, _isLocked, _members.Count, ownerName);
        }
        finally { _rwLock.ExitReadLock(); }
    }

    private static string HashPassword(string pw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("collab-salt:" + pw)));
    private static bool VerifyPassword(string? input, string stored) => input is not null && HashPassword(input) == stored;
    public void Dispose()
    {
        _rwLock.Dispose();
        Canvas.Dispose();
        Chat.Dispose(); 
    }
}
