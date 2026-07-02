using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CanvasServer.Models;

namespace CanvasServer.Core;

public sealed class CanvasState : IDisposable
{
    // Một nét vẽ đã lưu. IsActive quyết định có hiển thị hay không — Undo/Redo chỉ
    // bật/tắt cờ này, KHÔNG bao giờ xoá khỏi _allStrokes, để giữ đúng thứ tự vẽ gốc (z-order).
    private sealed class StrokeEntry
    {
        public required int    Sequence;
        public required string OwnerSessionId;
        public required byte[] Data;
        public bool IsActive = true;
    }

    private readonly ReaderWriterLockSlim _rwLock = new();

    // Nguồn sự thật duy nhất, đúng thứ tự vẽ.
    private readonly List<StrokeEntry> _allStrokes = [];
    private readonly Dictionary<int, StrokeEntry> _bySequence = [];

    // Mỗi user một ngăn xếp Redo riêng (LIFO) — hoàn toàn độc lập giữa các user,
    // đúng yêu cầu "Undo/Redo độc lập theo từng người" của đề tài.
    private readonly Dictionary<string, Stack<int>> _redoStacks = [];

    private int _currentSequence = 0;

    public int CurrentSequence
    {
        get { _rwLock.EnterReadLock(); try { return _currentSequence; } finally { _rwLock.ExitReadLock(); } }
    }

    public StrokePacket AppendStroke(string ownerSessionId, byte[] strokeData)
    {
        _rwLock.EnterWriteLock();
        try
        {
            _currentSequence++;
            var entry = new StrokeEntry
            {
                Sequence       = _currentSequence,
                OwnerSessionId = ownerSessionId,
                Data           = strokeData,
                IsActive       = true
            };
            _allStrokes.Add(entry);
            _bySequence[entry.Sequence] = entry;

            // Vẽ nét mới thì nhánh Redo cũ của CHÍNH user này hết hiệu lực (chuẩn hành vi editor).
            if (_redoStacks.TryGetValue(ownerSessionId, out var redoStack)) redoStack.Clear();

            return new StrokePacket(entry.Sequence, entry.OwnerSessionId, entry.Data);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    // Hoàn tác nét ACTIVE gần nhất của đúng owner. Trả về null nếu owner chưa có nét nào để hoàn tác.
    public (int Sequence, string OwnerSessionId)? UndoLast(string ownerSessionId)
    {
        _rwLock.EnterWriteLock();
        try
        {
            for (int i = _allStrokes.Count - 1; i >= 0; i--)
            {
                var entry = _allStrokes[i];
                if (entry.OwnerSessionId != ownerSessionId || !entry.IsActive) continue;

                entry.IsActive = false;
                if (!_redoStacks.TryGetValue(ownerSessionId, out var redoStack))
                {
                    redoStack = new Stack<int>();
                    _redoStacks[ownerSessionId] = redoStack;
                }
                redoStack.Push(entry.Sequence);
                return (entry.Sequence, entry.OwnerSessionId);
            }
            return null;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    // Làm lại nét vừa Undo gần nhất của đúng owner (LIFO). null nếu không còn gì để Redo.
    public StrokePacket? RedoLast(string ownerSessionId)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (!_redoStacks.TryGetValue(ownerSessionId, out var redoStack) || redoStack.Count == 0)
                return null;

            var sequence = redoStack.Pop();
            if (!_bySequence.TryGetValue(sequence, out var entry)) return null;

            entry.IsActive = true;
            return new StrokePacket(entry.Sequence, entry.OwnerSessionId, entry.Data);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    // Snapshot chỉ gồm các nét đang ACTIVE (đã ẩn hết những gì bị Undo), giữ nguyên thứ tự vẽ gốc.
    public (int Sequence, IReadOnlyList<StrokePacket> ActiveStrokes) GetSnapshot()
    {
        _rwLock.EnterReadLock();
        try
        {
            var active = _allStrokes
                .Where(s => s.IsActive)
                .Select(s => new StrokePacket(s.Sequence, s.OwnerSessionId, s.Data))
                .ToList();
            return (_currentSequence, active);
        }
        finally { _rwLock.ExitReadLock(); }
    }

    public void Dispose() => _rwLock.Dispose();
}
