using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoCutterApp.Editing
{
    /// <summary>Mot doan lien tuc [Start, End) cua video, duoc giu hoac bo.</summary>
    public sealed class Segment
    {
        public double Start;
        public double End;
        public bool Keep = true;
        /// <summary>Thu tu trong video xuat (0 = dau tien). Mac dinh trung thu tu thoi gian.</summary>
        public int Order;
        public double Length => End - Start;
        public Segment Clone() => new Segment { Start = Start, End = End, Keep = Keep, Order = Order };
    }

    /// <summary>
    /// Cac doan phu kin [0, Duration], noi tiep nhau. Diem cat = ranh gioi giua 2 doan.
    /// Moi thao tac sua deu luu lai de hoan tac / lam lai.
    /// </summary>
    public sealed class Project
    {
        private List<Segment> _segs = new List<Segment>();
        private readonly Stack<List<Segment>> _undo = new Stack<List<Segment>>();
        private readonly Stack<List<Segment>> _redo = new Stack<List<Segment>>();

        /// <summary>Khoang cach toi thieu giua 2 diem cat (giay) - tranh doan rong.</summary>
        public double MinGap = 0.001;
        public double Duration { get; private set; }
        public IReadOnlyList<Segment> Segments => _segs;
        public event Action? Changed;

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        public void Reset(double duration)
        {
            Duration = duration;
            _segs = new List<Segment> { new Segment { Start = 0, End = duration, Keep = true } };
            _undo.Clear(); _redo.Clear();
            RebuildOrder();
            Changed?.Invoke();
        }

        private List<Segment> Snapshot() => _segs.Select(s => s.Clone()).ToList();

        /// <summary>
        /// Khoi phuc danh sach doan da luu (phien lam viec). La 1 thao tac binh thuong:
        /// Ctrl+Z ngay sau do quay ve video nguyen ven.
        /// </summary>
        public bool Restore(IEnumerable<Segment> segs)
        {
            var list = segs.Where(s => s.End > s.Start).OrderBy(s => s.Start).Select(s => s.Clone()).ToList();
            if (list.Count == 0) return false;
            // phai noi tiep nhau va phu kin video (sai lech nho do lam tron thi Normalize sua)
            if (list[0].Start > 0.01 || Math.Abs(list[list.Count - 1].End - Duration) > 0.05) return false;
            for (int i = 1; i < list.Count; i++) if (Math.Abs(list[i].Start - list[i - 1].End) > 0.01) return false;
            var b = Snapshot();
            _segs = list;
            Commit(b);
            return true;
        }

        private void Commit(List<Segment> before)
        {
            _undo.Push(before);
            if (_undo.Count > 500) { var arr = _undo.ToArray(); _undo.Clear(); for (int i = 399; i >= 0; i--) _undo.Push(arr[i]); }
            _redo.Clear();
            Normalize();
            Changed?.Invoke();
        }

        public void Undo() { if (_undo.Count == 0) return; _redo.Push(Snapshot()); _segs = _undo.Pop(); RebuildOrder(); Changed?.Invoke(); }
        public void Redo() { if (_redo.Count == 0) return; _undo.Push(Snapshot()); _segs = _redo.Pop(); RebuildOrder(); Changed?.Invoke(); }

        /// <summary>Gop cac doan ke nhau cung trang thai? Khong - giu nguyen diem cat nguoi dung dat. Chi bo doan rong.</summary>
        private void Normalize()
        {
            _segs.RemoveAll(s => s.End - s.Start < MinGap / 2);
            if (_segs.Count == 0) _segs.Add(new Segment { Start = 0, End = Duration });
            _segs[0].Start = 0;
            _segs[_segs.Count - 1].End = Duration;
            for (int i = 1; i < _segs.Count; i++) _segs[i].Start = _segs[i - 1].End;
            RebuildOrder();
        }

        // ---------------------------------------------------------------- thu tu xuat

        private int[] _ordered = { 0 };   // vi tri trong danh sach -> chi so doan (theo thoi gian)
        private int[] _posOf = { 0 };     // chi so doan -> vi tri trong danh sach

        /// <summary>Danh lai Order = 0..n-1 theo (Order, Start): doan moi chia ra dung ngay sau doan goc.</summary>
        private void RebuildOrder()
        {
            var idx = Enumerable.Range(0, _segs.Count).OrderBy(i => _segs[i].Order).ThenBy(i => _segs[i].Start).ToArray();
            _ordered = idx;
            _posOf = new int[idx.Length];
            for (int p = 0; p < idx.Length; p++) { _posOf[idx[p]] = p; _segs[idx[p]].Order = p; }
        }

        /// <summary>Chi so doan (theo thoi gian) o vi tri pos trong danh sach xuat.</summary>
        public int SegmentAtPos(int pos) => pos >= 0 && pos < _ordered.Length ? _ordered[pos] : -1;
        /// <summary>Vi tri trong danh sach xuat cua doan index.</summary>
        public int PosOf(int index) => index >= 0 && index < _posOf.Length ? _posOf[index] : -1;
        /// <summary>Thu tu xuat khac thu tu thoi gian?</summary>
        public bool IsReordered => _ordered.Where((s, p) => s != p).Any();

        public bool CanMoveUp(int pos) => pos > 0 && pos < _ordered.Length;
        public bool CanMoveDown(int pos) => pos >= 0 && pos < _ordered.Length - 1;
        /// <summary>Gop duoc voi doan ngay tren trong danh sach khi 2 doan lien nhau ve thoi gian.</summary>
        public bool CanMergeUp(int pos) => pos > 0 && pos < _ordered.Length && Math.Abs(_ordered[pos] - _ordered[pos - 1]) == 1;

        public void MoveUp(int pos) => Swap(pos, pos - 1);
        public void MoveDown(int pos) => Swap(pos, pos + 1);

        private void Swap(int p, int q)
        {
            if (p < 0 || q < 0 || p >= _ordered.Length || q >= _ordered.Length || p == q) return;
            var b = Snapshot();
            int a = _ordered[p], c = _ordered[q];
            (_segs[a].Order, _segs[c].Order) = (_segs[c].Order, _segs[a].Order);
            Commit(b);
        }

        /// <summary>Gop doan o vi tri pos vao doan ngay tren no (lay trang thai giu/bo va vi tri cua doan tren).</summary>
        public bool MergeUp(int pos)
        {
            if (!CanMergeUp(pos)) return false;
            var b = Snapshot();
            int above = _ordered[pos - 1], me = _ordered[pos];
            int lo = Math.Min(above, me);
            bool keep = _segs[above].Keep; int order = _segs[above].Order;
            _segs[lo].End = _segs[lo + 1].End;
            _segs[lo].Keep = keep; _segs[lo].Order = order;
            _segs.RemoveAt(lo + 1);
            Commit(b);
            return true;
        }

        /// <summary>Xoa duoc khi doan dang giu, hoac doan bo nam canh mot doan bo khac (se gop lai).</summary>
        public bool CanDelete(int index)
        {
            if (index < 0 || index >= _segs.Count) return false;
            if (_segs[index].Keep) return true;
            return (index > 0 && !_segs[index - 1].Keep) || (index < _segs.Count - 1 && !_segs[index + 1].Keep);
        }

        /// <summary>
        /// Xoa doan khoi video xuat: danh dau bo va gop vao phan bo lien ke (theo thoi gian) de danh sach gon.
        /// Khac voi nut Giu (chi tam bo, giu nguyen ranh gioi).
        /// </summary>
        public bool DeleteSegment(int index)
        {
            if (!CanDelete(index)) return false;
            var b = Snapshot();
            _segs[index].Keep = false;
            if (index < _segs.Count - 1 && !_segs[index + 1].Keep)
            {
                _segs[index].End = _segs[index + 1].End;
                _segs[index].Order = Math.Min(_segs[index].Order, _segs[index + 1].Order);
                _segs.RemoveAt(index + 1);
            }
            if (index > 0 && !_segs[index - 1].Keep)
            {
                _segs[index - 1].End = _segs[index].End;
                _segs[index - 1].Order = Math.Min(_segs[index - 1].Order, _segs[index].Order);
                _segs.RemoveAt(index);
            }
            Commit(b);
            return true;
        }

        /// <summary>Dua thu tu xuat ve dung thu tu thoi gian.</summary>
        public void ResetOrder()
        {
            if (!IsReordered) return;
            var b = Snapshot();
            for (int i = 0; i < _segs.Count; i++) _segs[i].Order = i;
            Commit(b);
        }

        public int IndexAt(double t)
        {
            for (int i = 0; i < _segs.Count; i++) if (t < _segs[i].End || i == _segs.Count - 1) return i;
            return _segs.Count - 1;
        }

        public Segment At(double t) => _segs[IndexAt(t)];

        public IEnumerable<double> CutPoints() { for (int i = 1; i < _segs.Count; i++) yield return _segs[i].Start; }

        public bool IsCutPoint(double t, double tol = 0.0005) => CutPoints().Any(c => Math.Abs(c - t) <= tol);

        /// <summary>Chia doi doan tai t. Tra ve false neu t trung diem cat hoac sat mep.</summary>
        public bool Split(double t) => SplitInternal(t, null);

        private bool SplitInternal(double t, Action<Segment, Segment>? mark)
        {
            int i = IndexAt(t);
            var s = _segs[i];
            if (t - s.Start < MinGap || s.End - t < MinGap)
            {
                // Dung ngay diem cat: van cho phep danh dau I/O cho doan ben canh
                if (mark == null) return false;
                var before = Snapshot();
                if (t - s.Start < MinGap && i > 0) mark(_segs[i - 1], s);
                else if (s.End - t < MinGap && i < _segs.Count - 1) mark(s, _segs[i + 1]);
                else return false;
                Commit(before);
                return true;
            }
            var b = Snapshot();
            var right = new Segment { Start = t, End = s.End, Keep = s.Keep, Order = s.Order };
            s.End = t;
            _segs.Insert(i + 1, right);
            mark?.Invoke(s, right);
            Commit(b);
            return true;
        }

        /// <summary>Phim I: "giu tu day" - chia tai t, phan ben trai (trong doan hien tai) bi bo.</summary>
        public bool MarkIn(double t)
        {
            if (t <= MinGap) { var b = Snapshot(); _segs[0].Keep = true; Commit(b); return true; }
            return SplitInternal(t, (left, right) => { left.Keep = false; right.Keep = true; });
        }

        /// <summary>Phim O: "giu den day" - chia tai t, phan ben phai (trong doan hien tai) bi bo.</summary>
        public bool MarkOut(double t)
        {
            if (t >= Duration - MinGap) { var b = Snapshot(); _segs[_segs.Count - 1].Keep = true; Commit(b); return true; }
            return SplitInternal(t, (left, right) => { left.Keep = true; right.Keep = false; });
        }

        public void ToggleKeep(int index)
        {
            if (index < 0 || index >= _segs.Count) return;
            var b = Snapshot();
            _segs[index].Keep = !_segs[index].Keep;
            Commit(b);
        }

        public void SetKeep(int index, bool keep)
        {
            if (index < 0 || index >= _segs.Count || _segs[index].Keep == keep) return;
            var b = Snapshot();
            _segs[index].Keep = keep;
            Commit(b);
        }

        /// <summary>Xoa diem cat o dau doan index (gop voi doan truoc, lay trang thai doan truoc).</summary>
        public bool RemoveCutBefore(int index)
        {
            if (index <= 0 || index >= _segs.Count) return false;
            var b = Snapshot();
            _segs[index - 1].End = _segs[index].End;
            _segs.RemoveAt(index);
            Commit(b);
            return true;
        }

        /// <summary>Bat dau mot lan keo diem cat (luu trang thai de hoan tac 1 lan).</summary>
        private List<Segment>? _dragBefore;
        public void BeginMoveCut() => _dragBefore = Snapshot();

        /// <summary>Di chuyen diem cat o dau doan index toi t (gioi han giua 2 diem cat ke ben).</summary>
        public double MoveCut(int index, double t)
        {
            if (index <= 0 || index >= _segs.Count) return t;
            double lo = _segs[index - 1].Start + MinGap, hi = _segs[index].End - MinGap;
            t = Math.Max(lo, Math.Min(hi, t));
            _segs[index - 1].End = t;
            _segs[index].Start = t;
            Changed?.Invoke();
            return t;
        }

        public void EndMoveCut()
        {
            if (_dragBefore == null) return;
            var b = _dragBefore; _dragBefore = null;
            bool same = b.Count == _segs.Count && b.Zip(_segs, (x, y) => Math.Abs(x.Start - y.Start) < 1e-9).All(z => z);
            if (!same) Commit(b);
        }

        /// <summary>Dat chinh xac thoi diem bat dau / ket thuc cua doan (sua trong bang).</summary>
        public void SetBounds(int index, double start, double end)
        {
            if (index < 0 || index >= _segs.Count) return;
            var b = Snapshot();
            if (index > 0)
            {
                double lo = _segs[index - 1].Start + MinGap;
                start = Math.Max(lo, Math.Min(start, _segs[index].End - MinGap));
                _segs[index - 1].End = start; _segs[index].Start = start;
            }
            if (index < _segs.Count - 1)
            {
                double hi = _segs[index + 1].End - MinGap;
                end = Math.Min(hi, Math.Max(end, _segs[index].Start + MinGap));
                _segs[index].End = end; _segs[index + 1].Start = end;
            }
            Commit(b);
        }

        public void ClearAll()
        {
            var b = Snapshot();
            _segs = new List<Segment> { new Segment { Start = 0, End = Duration, Keep = true } };
            Commit(b);
        }

        public List<Segment> KeptSegments() => _ordered.Select(i => _segs[i]).Where(s => s.Keep && s.Length > MinGap).Select(s => s.Clone()).ToList();

        /// <summary>
        /// Cac doan giu theo thu tu xuat. Doan ke tiep nhau ca ve thu tu lan thoi gian duoc gop lai
        /// (xuat it doan hon, khong co moi noi thua).
        /// </summary>
        public List<Segment> KeptRanges()
        {
            var r = new List<Segment>();
            foreach (int i in _ordered)
            {
                var s = _segs[i];
                if (!s.Keep) continue;
                if (r.Count > 0 && Math.Abs(r[r.Count - 1].End - s.Start) < 1e-9) r[r.Count - 1].End = s.End;
                else r.Add(s.Clone());
            }
            return r;
        }

        public double KeptDuration => _segs.Where(s => s.Keep).Sum(s => s.Length);

        /// <summary>Thoi diem bat dau doan giu tiep theo tinh tu t (de "xem thu ket qua" nhay qua doan bo).</summary>
        public double? NextKeptStart(double t)
        {
            foreach (var s in _segs) if (s.Keep && s.End > t + 1e-6) return Math.Max(s.Start, t);
            return null;
        }
    }
}
