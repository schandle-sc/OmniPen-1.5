using System.Windows.Ink;

namespace OmniPen.Ink;

/// <summary>
/// Lịch sử Undo/Redo cho MỘT StrokeCollection, theo Command pattern.
///
/// Vì sao không lưu snapshot Stack&lt;StrokeCollection&gt;?
///   - Snapshot copy toàn bộ nét sau mỗi thao tác → RAM tăng O(số nét × số bước).
///   - Ở đây mỗi bước chỉ giữ THAM CHIẾU tới các nét được thêm/bớt (Stroke là object, không copy điểm).
///
/// Vì sao cần "nhóm" (BeginGroup/EndGroup)?
///   - Tẩy một phần (EraseByPoint) bắn StrokesChanged hàng chục lần trong một lần kéo tẩy
///     (mỗi lần cắt nét: Removed = nét gốc, Added = các mảnh). Gộp từ lúc nhấn đến lúc nhả
///     thành MỘT bước, để Ctrl+Z khôi phục cả đường tẩy chứ không phải từng mẩu.
/// </summary>
public sealed class InkHistory
{
    private const int MaxSteps = 200;

    private readonly StrokeCollection _target;
    private readonly LinkedList<InkChange> _undo = new(); // LinkedList để cắt bớt đầu cũ khi vượt MaxSteps
    private readonly Stack<InkChange> _redo = new();

    private InkChange? _group;
    private bool _suppress;

    public event EventHandler? StateChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public InkHistory(StrokeCollection target)
    {
        _target = target;
        _target.StrokesChanged += OnStrokesChanged;
    }

    /// <summary>Bắt đầu một cử chỉ (nhấn chuột/bút). Gọi lặp lại khi đã mở nhóm sẽ bị bỏ qua.</summary>
    public void BeginGroup() => _group ??= new InkChange();

    /// <summary>Kết thúc cử chỉ (nhả chuột/bút). Nhóm rỗng bị bỏ.</summary>
    public void EndGroup()
    {
        if (_group is null) return;
        InkChange g = _group;
        _group = null;
        if (!g.IsEmpty) Push(g);
    }

    public void Undo()
    {
        EndGroup();
        if (_undo.Last is null) return;
        InkChange change = _undo.Last.Value;
        _undo.RemoveLast();

        RunSuppressed(() =>
        {
            if (change.CustomUndo is not null) { change.CustomUndo(); return; }
            SafeRemove(change.Added);
            SafeAdd(change.Removed);
        });
        _redo.Push(change);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        EndGroup();
        if (_redo.Count == 0) return;
        InkChange change = _redo.Pop();

        RunSuppressed(() =>
        {
            if (change.CustomRedo is not null) { change.CustomRedo(); return; }
            SafeRemove(change.Removed);
            SafeAdd(change.Added);
        });
        _undo.AddLast(change);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ghi một bước tùy biến (VD chèn ảnh vào InkCanvas.Children – không phải nét nên StrokesChanged không bắt được).
    /// Thao tác đã được thực hiện xong trước khi gọi; <paramref name="undo"/>/<paramref name="redo"/> dùng để đảo lại.
    /// </summary>
    public void PushCustom(Action undo, Action redo)
    {
        EndGroup();
        Push(new InkChange { CustomUndo = undo, CustomRedo = redo });
    }

    /// <summary>Thực hiện thay đổi nét mà KHÔNG ghi lịch sử (dùng khi tự ghi một bước PushCustom gộp nhiều thao tác).</summary>
    public void RunWithoutHistory(Action action)
    {
        EndGroup();
        RunSuppressed(action);
    }

    /// <summary>Thay toàn bộ nội dung (VD khi mở file) mà KHÔNG ghi vào lịch sử, đồng thời xóa lịch sử.</summary>
    public void ReplaceAllWithoutHistory(StrokeCollection strokes)
    {
        RunSuppressed(() =>
        {
            _target.Clear();
            _target.Add(strokes);
        });
        _undo.Clear();
        _redo.Clear();
        _group = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_suppress) return;

        if (_group is not null)
        {
            _group.Merge(e.Added, e.Removed);
        }
        else
        {
            var single = new InkChange();
            single.Merge(e.Added, e.Removed);
            if (!single.IsEmpty) Push(single);
        }
    }

    private void Push(InkChange change)
    {
        _undo.AddLast(change);
        if (_undo.Count > MaxSteps) _undo.RemoveFirst();
        _redo.Clear(); // Thao tác mới ⇒ nhánh redo cũ không còn hợp lệ.
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RunSuppressed(Action action)
    {
        _suppress = true;
        try { action(); }
        finally { _suppress = false; }
    }

    // StrokeCollection.Remove(StrokeCollection) ném ArgumentException nếu có nét không thuộc collection.
    // Gỡ từng nét có kiểm tra để lịch sử không bao giờ làm crash app.
    private void SafeRemove(IEnumerable<Stroke> strokes)
    {
        foreach (Stroke s in strokes.ToList())
            if (_target.Contains(s)) _target.Remove(s);
    }

    private void SafeAdd(IEnumerable<Stroke> strokes)
    {
        foreach (Stroke s in strokes)
            if (!_target.Contains(s)) _target.Add(s);
    }

    /// <summary>
    /// Một bước lịch sử = tập nét được thêm + tập nét bị bớt (net effect).
    /// Merge triệt tiêu "thêm rồi bớt" trong cùng nhóm: các mảnh trung gian khi tẩy nhiều lần
    /// liên tiếp trên cùng một nét không được lưu, chỉ giữ nét gốc (bớt) và mảnh cuối cùng (thêm).
    /// </summary>
    private sealed class InkChange
    {
        public List<Stroke> Added { get; } = new();
        public List<Stroke> Removed { get; } = new();
        public Action? CustomUndo { get; init; }
        public Action? CustomRedo { get; init; }
        public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && CustomUndo is null;

        public void Merge(StrokeCollection added, StrokeCollection removed)
        {
            foreach (Stroke s in removed)
            {
                // Nét vừa được thêm trong cùng nhóm rồi bị bớt → coi như chưa từng tồn tại.
                if (!Added.Remove(s)) Removed.Add(s);
            }
            Added.AddRange(added);
        }
    }
}
