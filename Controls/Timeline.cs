using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VideoCutterApp.Editing;
using VideoCutterApp.Export;
using VideoCutterApp.Media;

namespace VideoCutterApp.Controls
{
    /// <summary>
    /// Timeline ve tay (OnRender) de muot voi video dai:
    /// thuoc thoi gian, cac doan giu/bo, diem cat keo duoc (bat dinh khung/dau phat/keyframe),
    /// vach keyframe, dau phat, phong to quanh con tro (Ctrl+lan chuot), thanh tong quan.
    /// </summary>
    public sealed class Timeline : FrameworkElement
    {
        private const double RulerH = 24, TrackTop = 28, TrackH = 54, KeyRowH = 9, OverviewH = 8, HandleW = 6;

        public Project? Project { get; set; }
        public MediaInfo? Media { get; set; }
        public double Playhead { get; private set; }
        public int SelectedIndex { get; set; } = -1;
        /// <summary>Doan thuc te khi xuat lossless (ve phan thua do bat dinh keyframe).</summary>
        public IList<PlannedRange>? LosslessRanges { get; set; }

        public double ViewStart { get; private set; }
        public double ViewLength { get; private set; } = 1;

        /// <summary>(thoi diem, chinh xac?) - false khi dang keo (tua nhanh), true khi tha chuot.</summary>
        public event Action<double, bool>? SeekRequested;
        public event Action<int>? SegmentClicked;
        public event Action<int>? SegmentDoubleClicked;
        public event Action? CutsEdited;
        public event Action<string>? HoverText;
        /// <summary>(chi so diem cat, thoi diem, tha chuot?) - khi bam giu / keo diem cat: hien hinh tai do.</summary>
        public event Action<int, double, bool>? CutPreview;

        /// <summary>Diem cat dang chon (chi so doan ben phai diem cat), -1 = khong co. Dung , . de dich tung khung.</summary>
        public int SelectedCut { get; set; } = -1;

        private enum Drag { None, Scrub, Cut, Overview }
        private Drag _drag;
        private int _dragCut = -1;
        private double _hoverX = -1;
        private double _lastScrubSeek;

        private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x1A, 0x24)));
        private static readonly Brush RulerBg = Frozen(new SolidColorBrush(Color.FromRgb(0x20, 0x25, 0x33)));
        private static readonly Brush KeepFill = Frozen(new LinearGradientBrush(Color.FromRgb(0x2F, 0xBF, 0x8F), Color.FromRgb(0x1F, 0x8F, 0x75), 90));
        private static readonly Brush KeepFillSel = Frozen(new LinearGradientBrush(Color.FromRgb(0x4A, 0xDF, 0xAB), Color.FromRgb(0x28, 0xA8, 0x88), 90));
        private static readonly Brush DropFill = Frozen(Hatch(Color.FromRgb(0x33, 0x38, 0x4A), Color.FromRgb(0x44, 0x4A, 0x60)));
        private static readonly Brush ExtraFill = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xC2, 0x66)));
        private static readonly Brush TextBr = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFB)));
        private static readonly Brush MutedBr = Frozen(new SolidColorBrush(Color.FromRgb(0xB4, 0xBC, 0xD0)));
        private static readonly Brush KeyBr = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x66)));
        private static readonly Brush PlayBr = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x74)));
        private static readonly Brush HoverBr = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush ViewBr = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0xA0, 0x8A, 0xFF)));
        private static readonly Pen TickPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x8C)), 1);
        private static readonly Pen MinorPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0x45, 0x4C, 0x62)), 1);
        private static readonly Pen CutPen = FrozenPen(Brushes.White, 2);
        private static readonly Pen SelPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0xA0, 0x8A, 0xFF)), 2);
        private static readonly Pen PlayPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x74)), 2);
        private static readonly Pen KeyPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x66)), 1);
        private static readonly Pen HoverPen = FrozenPen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1);
        private static readonly Brush SelCutBr = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x66)));
        private static readonly Pen SelCutPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x66)), 3);
        private static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface Mono = new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        private static T Frozen<T>(T b) where T : Freezable { b.Freeze(); return b; }
        private static Pen FrozenPen(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }

        private static Brush Hatch(Color bg, Color line)
        {
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(bg), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            g.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(line), 2), new LineGeometry(new Point(0, 8), new Point(8, 0))));
            return new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        }

        public Timeline()
        {
            Focusable = false;
            ClipToBounds = true;
            Cursor = Cursors.Arrow;
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new Size(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, TrackTop + TrackH + KeyRowH + OverviewH + 8);

        private double Duration => Project?.Duration ?? 0;
        private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // ---------------------------------------------------------------- toa do

        private double X(double t) => (t - ViewStart) / ViewLength * ActualWidth;
        private double T(double x) => ViewStart + x / Math.Max(1, ActualWidth) * ViewLength;

        public void ResetView()
        {
            ViewStart = 0;
            ViewLength = Math.Max(0.5, Duration);
            InvalidateVisual();
        }

        public void SetPlayhead(double t, bool follow)
        {
            Playhead = t;
            if (follow && Duration > 0 && _drag == DragNone)
            {
                if (t > ViewStart + ViewLength * 0.97) SetView(t - ViewLength * 0.05, ViewLength);
                else if (t < ViewStart) SetView(t - ViewLength * 0.05, ViewLength);
            }
            InvalidateVisual();
        }

        private const Drag DragNone = Drag.None;

        public void SetView(double start, double length)
        {
            double d = Math.Max(0.5, Duration);
            double minLen = Media != null && Media.Fps > 0 ? Math.Max(0.2, 12.0 / Media.Fps) : 0.4;
            length = Math.Max(minLen, Math.Min(d, length));
            start = Math.Max(0, Math.Min(d - length, start));
            ViewStart = start; ViewLength = length;
            InvalidateVisual();
        }

        /// <summary>Phong to (factor &lt; 1) / thu nho quanh thoi diem t.</summary>
        public void Zoom(double factor, double? aroundT = null)
        {
            double t = aroundT ?? Playhead;
            double rel = ViewLength > 0 ? (t - ViewStart) / ViewLength : 0.5;
            if (aroundT == null) rel = 0.5;
            double len = ViewLength * factor;
            SetView(t - rel * len, len);
        }

        public void ZoomToRange(double a, double b)
        {
            double pad = (b - a) * 0.08 + 0.2;
            SetView(a - pad, b - a + 2 * pad);
        }

        // ---------------------------------------------------------------- ve

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(RulerBg, null, new Rect(0, 0, w, RulerH));
            if (Project == null || Duration <= 0) return;

            DrawRuler(dc, w);

            // Doan
            var segs = Project.Segments;
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                double x0 = X(s.Start), x1 = X(s.End);
                if (x1 < 0 || x0 > w) continue;
                var r = new Rect(Math.Max(-2, x0), TrackTop, Math.Max(1, Math.Min(w + 2, x1) - Math.Max(-2, x0)), TrackH);
                dc.DrawRoundedRectangle(s.Keep ? (i == SelectedIndex ? KeepFillSel : KeepFill) : DropFill, null, r, 4, 4);
                if (i == SelectedIndex) dc.DrawRoundedRectangle(null, SelPen, new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), r.Height - 2), 4, 4);
                if (r.Width > 54)
                {
                    int no = Project.PosOf(i) + 1;
                    string label = $"{no}  {(s.Keep ? "" : "bỏ  ")}{Fmt.Duration(s.Length)}";
                    var ft = Text(label, 12, s.Keep ? TextBr : MutedBr, Face);
                    if (ft.Width < r.Width - 10) dc.DrawText(ft, new Point(Math.Max(r.X, 0) + 7, TrackTop + 6));
                }
            }

            // Phan thua khi xuat lossless (bat dinh keyframe)
            if (LosslessRanges != null)
            {
                foreach (var pr in LosslessRanges)
                {
                    if (pr.ActualStart < pr.Start - 1e-4) Band(dc, pr.ActualStart, pr.Start, w);
                    if (pr.ActualEnd > pr.End + 1e-4) Band(dc, pr.End, pr.ActualEnd, w);
                }
            }

            // Diem cat
            for (int i = 1; i < segs.Count; i++)
            {
                double x = X(segs[i].Start);
                if (x < -4 || x > w + 4) continue;
                if (i == SelectedCut)
                {
                    dc.DrawLine(SelCutPen, new Point(x, TrackTop - 4), new Point(x, TrackTop + TrackH + 4));
                    dc.DrawRoundedRectangle(SelCutBr, null, new Rect(x - 6, TrackTop - 7, 12, 14), 3, 3);
                    dc.DrawRoundedRectangle(SelCutBr, null, new Rect(x - 6, TrackTop + TrackH - 7, 12, 14), 3, 3);
                    continue;
                }
                dc.DrawLine(CutPen, new Point(x, TrackTop - 2), new Point(x, TrackTop + TrackH + 2));
                dc.DrawRoundedRectangle(Brushes.White, null, new Rect(x - HandleW / 2, TrackTop - 4, HandleW, 10), 2, 2);
            }

            // Keyframe
            var keys = Media?.Keyframes;
            double keyY = TrackTop + TrackH + 2;
            if (keys != null && keys.Length > 0)
            {
                int i0 = Array.BinarySearch(keys, ViewStart); if (i0 < 0) i0 = ~i0;
                int i1 = Array.BinarySearch(keys, ViewStart + ViewLength); if (i1 < 0) i1 = ~i1;
                int n = i1 - i0;
                if (n > 0 && w / n >= 2.5)
                    for (int i = i0; i < i1; i++) { double x = X(keys[i]); dc.DrawLine(KeyPen, new Point(x, keyY), new Point(x, keyY + KeyRowH - 2)); }
            }

            // Thanh tong quan (vung dang xem)
            double oy = h - OverviewH - 2;
            dc.DrawRoundedRectangle(RulerBg, null, new Rect(0, oy, w, OverviewH), 3, 3);
            double vx0 = ViewStart / Duration * w, vx1 = (ViewStart + ViewLength) / Duration * w;
            dc.DrawRoundedRectangle(ViewBr, null, new Rect(vx0, oy, Math.Max(6, vx1 - vx0), OverviewH), 3, 3);
            double px = Playhead / Duration * w;
            dc.DrawRectangle(PlayBr, null, new Rect(px - 1, oy - 1, 2, OverviewH + 2));

            // Chuot
            if (_hoverX >= 0 && _drag == Drag.None)
                dc.DrawLine(HoverPen, new Point(_hoverX, RulerH), new Point(_hoverX, TrackTop + TrackH + KeyRowH));

            // Dau phat
            double phx = X(Playhead);
            if (phx >= -2 && phx <= w + 2)
            {
                dc.DrawLine(PlayPen, new Point(phx, 0), new Point(phx, TrackTop + TrackH + KeyRowH));
                var tri = new StreamGeometry();
                using (var g = tri.Open()) { g.BeginFigure(new Point(phx - 6, 0), true, true); g.LineTo(new Point(phx + 6, 0), true, false); g.LineTo(new Point(phx, 9), true, false); }
                tri.Freeze();
                dc.DrawGeometry(PlayBr, null, tri);
            }
        }

        private void Band(DrawingContext dc, double a, double b, double w)
        {
            double x0 = X(a), x1 = X(b);
            if (x1 < 0 || x0 > w) return;
            dc.DrawRectangle(ExtraFill, null, new Rect(x0, TrackTop + TrackH - 8, Math.Max(2, x1 - x0), 8));
        }

        private void DrawRuler(DrawingContext dc, double w)
        {
            double fps = Media != null && Media.Fps > 0 ? Media.Fps : 30;
            double[] steps = { 1 / fps, 2 / fps, 5 / fps, 10 / fps, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200 };
            double pxPerSec = w / ViewLength;
            double step = steps.FirstOrDefault(s => s * pxPerSec >= 90);
            if (step <= 0) step = 7200;
            double minor = step / 5;
            double t0 = Math.Floor(ViewStart / minor) * minor;
            for (double t = t0; t <= ViewStart + ViewLength + minor; t += minor)
            {
                double x = X(t);
                bool major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
                dc.DrawLine(major ? TickPen : MinorPen, new Point(x, major ? 12 : 18), new Point(x, RulerH));
                if (major && t >= -1e-9)
                {
                    var ft = Text(Fmt.Ruler(t, step < 1 ? fps : 0), 10.5, MutedBr, Mono);
                    dc.DrawText(ft, new Point(x + 3, 1));
                }
            }
        }

        private FormattedText Text(string s, double size, Brush br, Typeface tf) =>
            new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, br, Dpi);

        // ---------------------------------------------------------------- chuot

        private int CutNear(double x)
        {
            if (Project == null) return -1;
            var segs = Project.Segments;
            int best = -1; double bd = 7;
            for (int i = 1; i < segs.Count; i++) { double d = Math.Abs(X(segs[i].Start) - x); if (d < bd) { bd = d; best = i; } }
            return best;
        }

        private double SnapTime(double t, double x, bool snapPlayhead = true)
        {
            if (Media == null) return t;
            // bat dinh vao dau phat / keyframe trong pham vi 6 px
            double tol = 6 * ViewLength / Math.Max(1, ActualWidth);
            if (snapPlayhead && Math.Abs(t - Playhead) < tol) return Playhead;
            var k = Media.Keyframes;
            if (k.Length > 0)
            {
                double kb = Media.KeyframeAtOrBefore(t), ka = Media.KeyframeAtOrAfter(t);
                if (Math.Abs(t - kb) < tol) return kb;
                if (Math.Abs(ka - t) < tol && ka < Duration) return ka;
            }
            return Media.SnapToFrame(t);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (Project == null || Duration <= 0) return;
            var p = e.GetPosition(this);
            CaptureMouse();
            if (p.Y >= ActualHeight - OverviewH - 6)
            {
                _drag = Drag.Overview;
                OverviewTo(p.X);
                return;
            }
            int cut = p.Y >= TrackTop - 6 && p.Y <= TrackTop + TrackH ? CutNear(p.X) : -1;
            if (cut > 0)
            {
                _drag = Drag.Cut; _dragCut = cut;
                Project.BeginMoveCut();
                SelectedIndex = cut;
                SelectedCut = cut;
                _lastScrubSeek = Environment.TickCount;
                CutPreview?.Invoke(cut, Project.Segments[cut].Start, false);
                InvalidateVisual();
                return;
            }
            SelectedCut = -1;
            double t = Math.Max(0, Math.Min(Duration, T(p.X)));
            if (p.Y >= TrackTop && p.Y <= TrackTop + TrackH)
            {
                int idx = Project.IndexAt(t);
                SelectedIndex = idx;
                if (e.ClickCount == 2) { SegmentDoubleClicked?.Invoke(idx); _drag = Drag.None; ReleaseMouseCapture(); InvalidateVisual(); return; }
                SegmentClicked?.Invoke(idx);
            }
            _drag = Drag.Scrub;
            Playhead = t;
            SeekRequested?.Invoke(t, false);
            InvalidateVisual();
        }

        private void OverviewTo(double x)
        {
            double t = x / Math.Max(1, ActualWidth) * Duration;
            SetView(t - ViewLength / 2, ViewLength);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (Project == null || Duration <= 0) return;
            double t = Math.Max(0, Math.Min(Duration, T(p.X)));
            switch (_drag)
            {
                case Drag.Overview: OverviewTo(p.X); return;
                case Drag.Cut:
                    // dau phat chay theo diem cat nen khong bat dinh vao dau phat
                    t = Project.MoveCut(_dragCut, SnapTime(t, p.X, false));
                    HoverText?.Invoke("Điểm cắt: " + Fmt.Time(t, Media));
                    // hinh chay theo diem cat (toi da ~25 lan/giay)
                    if (Environment.TickCount - _lastScrubSeek > 40) { _lastScrubSeek = Environment.TickCount; CutPreview?.Invoke(_dragCut, t, false); }
                    InvalidateVisual();
                    return;
                case Drag.Scrub:
                    Playhead = t;
                    // Tua nhanh toi da ~25 lan/giay khi keo
                    if (Environment.TickCount - _lastScrubSeek > 40) { _lastScrubSeek = Environment.TickCount; SeekRequested?.Invoke(t, false); }
                    InvalidateVisual();
                    return;
            }
            _hoverX = p.X;
            bool onCut = p.Y >= TrackTop - 6 && p.Y <= TrackTop + TrackH && CutNear(p.X) > 0;
            Cursor = onCut ? Cursors.SizeWE : Cursors.Arrow;
            HoverText?.Invoke(Fmt.Time(t, Media) + (onCut ? "   •   kéo để chỉnh điểm cắt" : ""));
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            var d = _drag;
            _drag = Drag.None;
            ReleaseMouseCapture();
            if (Project == null) return;
            if (d == Drag.Cut)
            {
                Project.EndMoveCut();
                CutsEdited?.Invoke();
                if (SelectedCut > 0 && SelectedCut < Project.Segments.Count) CutPreview?.Invoke(SelectedCut, Project.Segments[SelectedCut].Start, true);
            }
            else if (d == Drag.Scrub)
            {
                double t = Math.Max(0, Math.Min(Duration, T(e.GetPosition(this).X)));
                t = Media != null ? Media.SnapToFrame(t) : t;
                Playhead = t;
                SeekRequested?.Invoke(t, true);
            }
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e) { _hoverX = -1; HoverText?.Invoke(""); InvalidateVisual(); }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (Duration <= 0) return;
            var p = e.GetPosition(this);
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 || (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && ViewLength >= Duration - 1e-6)
                Zoom(e.Delta > 0 ? 0.7 : 1 / 0.7, T(p.X));
            else
                SetView(ViewStart - e.Delta / 120.0 * ViewLength * 0.12, ViewLength);
            e.Handled = true;
        }
    }

    /// <summary>Dinh dang thoi gian.</summary>
    public static class Fmt
    {
        public static string Time(double t, MediaInfo? mi = null)
        {
            if (t < 0) t = 0;
            var ts = TimeSpan.FromSeconds(t);
            string s = $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
            if (mi != null && mi.HasVideo) s += $"  •  khung {mi.FrameIndexAt(t):N0}".Replace(',', '.');
            return s;
        }

        public static string Clock(double t)
        {
            if (t < 0) t = 0;
            var ts = TimeSpan.FromSeconds(t + 1e-7);
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
        }

        public static string Duration(double t)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, t));
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes}:{ts.Seconds:00}";
            return $"{ts.TotalSeconds:0.0}s".Replace('.', ',');
        }

        public static string Ruler(double t, double fps)
        {
            var ts = TimeSpan.FromSeconds(t + 1e-7);
            string hms = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
            if (fps > 0)
            {
                int f = (int)Math.Round((t - Math.Floor(t + 1e-7)) * fps);
                return hms + "." + f.ToString("00");
            }
            return hms;
        }

        /// <summary>Doc "1:02:03.500", "62.5", "1:30", "00:01:02,25" -> giay.</summary>
        public static bool TryParse(string s, out double t)
        {
            t = 0;
            s = s.Trim().Replace(',', '.');
            if (s.Length == 0) return false;
            var parts = s.Split(':');
            double acc = 0;
            foreach (var p in parts)
            {
                if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
                acc = acc * 60 + v;
            }
            t = acc;
            return true;
        }
    }
}
