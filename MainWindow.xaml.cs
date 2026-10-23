using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoCutterApp.Controls;
using VideoCutterApp.Editing;
using VideoCutterApp.Export;
using VideoCutterApp.Media;
using VideoCutterApp.Player;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace VideoCutterApp
{
    /// <summary>Mot dong trong danh sach doan.</summary>
    public sealed class SegItem
    {
        public int Index { get; set; }
        public string Number { get; set; } = "";
        public Brush Badge { get; set; } = Brushes.Transparent;
        public Brush BadgeText { get; set; } = Brushes.White;
        public string Range { get; set; } = "";
        public Brush RangeBrush { get; set; } = Brushes.White;
        public string Detail { get; set; } = "";
        /// <summary>Do dai doan (hien dam o dong 2).</summary>
        public string Length { get; set; } = "";
        public Brush LengthBrush { get; set; } = Brushes.White;
        public bool Keep { get; set; }
        /// <summary>Vi tri trong danh sach (thu tu xuat).</summary>
        public int Pos { get; set; }
        public bool CanUp { get; set; }
        public bool CanDown { get; set; }
        public bool CanMerge { get; set; }
        public bool CanDelete { get; set; }
        /// <summary>Thanh nho: phan truoc / doan / phan sau trong video goc (ti le).</summary>
        public GridLength PreW { get; set; }
        public GridLength LenW { get; set; }
        public GridLength PostW { get; set; }
        public Brush BarBrush { get; set; } = Brushes.Gray;
    }

    public sealed class ShortcutItem
    {
        public string Key { get; set; } = "";
        public string Desc { get; set; } = "";
        public ShortcutItem(string k, string d) { Key = k; Desc = d; }
    }

    /// <summary>Mot dong "Tiep tuc lam viec" o man hinh trong.</summary>
    public sealed class RecentItem
    {
        public string Path { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string Name { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    public partial class MainWindow : Window
    {
        private MpvPlayer? _player;
        private MediaInfo? _media;
        private readonly Project _project = new Project();
        private readonly ObservableCollection<SegItem> _items = new ObservableCollection<SegItem>();
        private readonly DispatcherTimer _tick;
        private readonly DispatcherTimer _reverse;
        private CancellationTokenSource? _framesCts;
        private Task? _framesTask;
        private CancellationTokenSource? _exportCts;
        private bool _ready, _suppressList, _muted;
        private int _curSeg = -1;
        private int _shuttle;               // >0 tua toi (x1,2,4,8), <0 tua lui, 0 binh thuong
        private double? _stopAt;            // dung phat tai thoi diem nay (xem cho noi)
        private int _stopItem = -1;         // ... khi dang o doan giu thu _stopItem (thu tu xuat)
        private int _playItem = -1;         // doan giu dang phat khi xem thu (thu tu xuat)
        private bool _joinCheck;            // dang xem cho noi -> nhay qua doan bo
        private int _lastSkipTick;
        private double _lastShown = -1;
        private bool _lastPaused = true;
        private string _autoOutput = "";
        private string? _lastResult;
        private readonly Settings _settings = Settings.Load();

        // Phien lam viec (tu luu)
        private readonly DispatcherTimer _saveTimer;
        private string? _sessionFp;          // dau van tay video dang mo (null = chua san sang / khong luu)
        private bool _restoring;
        private double? _pendingSeek;        // tua toi day khi trinh phat mo xong file
        private string _restoreNote = "";
        private static readonly string? TestSessionDir = Environment.GetEnvironmentVariable("VC_SESSION_DIR");
        /// <summary>Che do --snapshot khong dong vao phien that (tru khi chi dinh VC_SESSION_DIR de kiem thu).</summary>
        private static bool SessionsEnabled => App.SnapshotPath == null || !string.IsNullOrEmpty(TestSessionDir);

        private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

        public MainWindow()
        {
            InitializeComponent();

            // Kich thuoc mac dinh theo man hinh (1920x1080 -> gan toan man hinh)
            var wa = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, Math.Min(1880, wa.Width - 40));
            Height = Math.Max(MinHeight, Math.Min(1020, wa.Height - 30));
            MainGrid.SizeChanged += (s, e) => LayoutColumns();
            TransportCard.SizeChanged += (s, e) => LayoutColumns();

            SegmentList.ItemsSource = _items;
            if (!string.IsNullOrEmpty(TestSessionDir)) SessionStore.DirOverride = TestSessionDir;
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveSession(); };
            ShortcutList.ItemsSource = Shortcuts();

            TimelineView.Project = _project;
            TimelineView.SeekRequested += (t, exact) =>
            {
                if (_player == null) return;
                StopShuttle();
                // keo chuot tren timeline dai: tua toi keyframe cho muot; khi tha / da phong to: chinh xac
                if (exact || TimelineView.ViewLength < 90) _player.SeekExact(t); else _player.SeekFast(t);
                UpdatePosition(true);
            };
            TimelineView.SegmentDoubleClicked += i => ZoomToSegment(i);
            TimelineView.CutsEdited += () => Status("Đã chỉnh điểm cắt. Ctrl+Z để hoàn tác.");
            TimelineView.HoverText += s => HoverText.Text = s;
            TimelineView.CutPreview += OnCutPreview;

            _project.Changed += OnProjectChanged;

            _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _tick.Tick += (s, e) => OnTick();
            _reverse = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(70) };
            _reverse.Tick += (s, e) => OnReverseTick();

            // Cai dat da luu
            VolumeSlider.Value = Math.Max(0, Math.Min(130, _settings.Volume));
            _muted = _settings.Muted;
            UpdateMuteIcon();
            switch (_settings.Mode)
            {
                case ExportMode.Lossless: ModeLossless.IsChecked = true; break;
                case ExportMode.ExactSmall: ModeSmall.IsChecked = true; break;
                default: ModeExact.IsChecked = true; break;
            }

            PreviewKeyDown += OnPreviewKeyDown;
            Drop += OnWpfDrop;
            DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            MouseDown += (s, e) => { if (!(e.OriginalSource is TextBox)) FocusSink.Focus(); };

            VideoPanel.AllowDrop = true;
            VideoPanel.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop)
                ? System.Windows.Forms.DragDropEffects.Copy : System.Windows.Forms.DragDropEffects.None;
            VideoPanel.DragDrop += (s, e) =>
            {
                if (e.Data.GetData(System.Windows.Forms.DataFormats.FileDrop) is string[] f && f.Length > 0)
                    Dispatcher.BeginInvoke(new Action(() => _ = OpenFileAsync(f[0])));
            };
            VideoPanel.MouseDown += (s, e) =>
            {
                if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
                Dispatcher.BeginInvoke(new Action(() => { TogglePlay(); FocusSink.Focus(); }));
            };

            Loaded += OnLoaded;
            Closing += OnClosing;
            UpdateUndoButtons();
            _ready = true;
            UpdateModeHint();
        }

        private static List<ShortcutItem> Shortcuts() => new List<ShortcutItem>
        {
            new ShortcutItem("Space", "Phát / dừng"),
            new ShortcutItem("J  K  L", "Tua lùi / dừng / tua tới (bấm thêm = nhanh hơn)"),
            new ShortcutItem("← →", "Lùi / tới 1 khung hình"),
            new ShortcutItem("Shift + ← →", "Lùi / tới 1 giây"),
            new ShortcutItem("Ctrl + ← →", "Lùi / tới 10 giây"),
            new ShortcutItem("Alt + ← →", "Keyframe trước / sau"),
            new ShortcutItem("↑ ↓", "Điểm cắt trước / sau (và chọn điểm cắt đó)"),
            new ShortcutItem(",  .", "Dịch điểm cắt đang chọn 1 khung (Shift: 10 khung)"),
            new ShortcutItem("Home  End", "Về đầu / cuối video"),
            new ShortcutItem("I", "Giữ từ đây (bỏ phần trước)"),
            new ShortcutItem("O", "Giữ đến đây (gồm khung đang xem)"),
            new ShortcutItem("S  /  Ctrl+B", "Chia đoạn tại đầu phát"),
            new ShortcutItem("Delete", "Bỏ / giữ đoạn tại đầu phát"),
            new ShortcutItem("Shift + Delete", "Xoá hẳn đoạn tại đầu phát"),
            new ShortcutItem("P", "Xem thử kết quả (nhảy qua đoạn bỏ)"),
            new ShortcutItem("C", "Xem chỗ nối gần nhất (±2 giây)"),
            new ShortcutItem("M", "Tắt / bật tiếng"),
            new ShortcutItem("Ctrl+Z  Ctrl+Y", "Hoàn tác / làm lại"),
            new ShortcutItem("+  −", "Phóng to / thu nhỏ timeline"),
            new ShortcutItem("Ctrl+0", "Xem toàn bộ timeline"),
            new ShortcutItem("Ctrl+O", "Mở video"),
            new ShortcutItem("Ctrl+E", "Xuất video"),
            new ShortcutItem("Ô thời gian", "Gõ 1:23.5 hoặc #1234 rồi Enter"),
            new ShortcutItem("F1", "Hiện bảng phím tắt"),
        };

        // =====================================================================================
        // Khoi dong / dong
        // =====================================================================================

        /// <summary>
        /// Cot video rong vua du hien video theo dung ti le (mac dinh 16:9) o chieu cao hien co;
        /// phan be ngang con lai danh cho panel phai (460..760 px).
        /// </summary>
        private void LayoutColumns()
        {
            double w = MainGrid.ActualWidth, h = MainGrid.ActualHeight;
            if (w <= 0 || h <= 0) return;
            const double gap = 16, minPanel = 460, maxPanel = 760;
            double aspect = 16.0 / 9;
            if (_media != null && _media.HasVideo && _media.Width > 0 && _media.Height > 0) aspect = (double)_media.Width / _media.Height;
            double videoH = Math.Max(120, h - TransportCard.ActualHeight - TransportCard.Margin.Top - 2);
            double videoW = videoH * aspect + 2;
            double panel = Math.Max(minPanel, Math.Min(maxPanel, w - gap - videoW));
            panel = Math.Min(panel, Math.Max(minPanel, w - gap - 480));   // cua so hep: van chua cho video
            if (Math.Abs(PanelCol.Width.Value - panel) > 0.5) PanelCol.Width = new GridLength(Math.Round(panel));
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            FocusSink.Focus();
            _ = Encoders.AvailableAsync().ContinueWith(_ => Dispatcher.BeginInvoke(new Action(UpdateModeHint)));
            if (!File.Exists(Tools.FFmpeg) || !File.Exists(Tools.FFprobe))
                Status("Không thấy ffmpeg.exe / ffprobe.exe cạnh VideoCutter.exe — hãy giải nén đầy đủ thư mục.");

            if (App.StartupFile != null) await OpenFileAsync(App.StartupFile);
            else ShowRecent();
            if (App.SnapshotPath != null) await SnapshotAndExitAsync(App.SnapshotPath);
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_exportCts != null)
            {
                if (MessageBox.Show(this, "Đang xuất video. Huỷ và thoát?", "Video Cutter", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                { e.Cancel = true; return; }
                _exportCts.Cancel();
            }
            _tick.Stop();
            _reverse.Stop();
            _framesCts?.Cancel();
            _saveTimer.Stop();
            SaveSession();
            SaveSettings();
            try { _player?.Dispose(); } catch { }
            _player = null;
        }

        private void SaveSettings()
        {
            _settings.Mode = CurrentMode;
            _settings.Volume = VolumeSlider.Value;
            _settings.Muted = _muted;
            _settings.Save();
        }

        private bool EnsurePlayer()
        {
            if (_player != null) return true;
            try
            {
                VideoHost.Visibility = Visibility.Visible;
                VideoHost.UpdateLayout();
                var h = VideoPanel.Handle; // tao HWND
                _player = new MpvPlayer(h);
                _player.Log += s => App.WriteLog(s);
                _player.FileLoaded += () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    LoadingText.Visibility = Visibility.Collapsed;
                    ApplyVolume();
                    if (_pendingSeek is double t) { _pendingSeek = null; Pause(); SeekTo(t); }
                }));
                _player.EndFile += r =>
                {
                    if (r != "ok") Dispatcher.BeginInvoke(new Action(() =>
                    {
                        LoadingText.Visibility = Visibility.Collapsed;
                        Status("Trình phát không mở được video: " + r);
                    }));
                };
                _tick.Start();
                return true;
            }
            catch (Exception ex)
            {
                App.WriteLog("mpv: " + ex);
                VideoHost.Visibility = Visibility.Collapsed;
                MessageBox.Show(this, "Không khởi động được trình phát (libmpv-2.dll).\n" + ex.Message, "Video Cutter",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // =====================================================================================
        // Mo file
        // =====================================================================================

        private static readonly string[] VideoExts = { ".mp4", ".mkv", ".mov", ".m4v", ".webm", ".avi", ".ts", ".mts", ".m2ts", ".flv", ".wmv", ".mpg", ".mpeg", ".3gp" };

        private async Task OpenFileAsync(string path)
        {
            if (_exportCts != null) { Status("Đang xuất video — hãy đợi xong hoặc huỷ trước khi mở file khác."); return; }
            if (!File.Exists(path)) { Status("Không tìm thấy file: " + path); return; }

            EmptyState.Visibility = Visibility.Collapsed;
            LoadingText.Text = "Đang mở " + Path.GetFileName(path) + "…";
            LoadingText.Visibility = Visibility.Visible;
            Status("Đang đọc thông tin video…");

            MediaInfo mi;
            try { mi = await MediaInfo.ProbeAsync(path); }
            catch (Exception ex)
            {
                LoadingText.Visibility = Visibility.Collapsed;
                if (_media == null) EmptyState.Visibility = Visibility.Visible;
                App.WriteLog("probe: " + ex);
                MessageBox.Show(this, "Không đọc được file này.\n" + ex.Message, "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (mi.Duration <= 0 || (!mi.HasVideo && mi.Audio.Count == 0))
            {
                LoadingText.Visibility = Visibility.Collapsed;
                if (_media == null) EmptyState.Visibility = Visibility.Visible;
                MessageBox.Show(this, "File không có hình / tiếng hoặc không xác định được độ dài.", "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!EnsurePlayer()) { LoadingText.Visibility = Visibility.Collapsed; return; }

            _framesCts?.Cancel();
            StopShuttle();
            _stopAt = null; _joinCheck = false;
            PreviewToggle.IsChecked = false;
            ExportDonePanel.Visibility = Visibility.Collapsed;

            // luu phien cua video dang mo truoc khi chuyen sang video khac
            _saveTimer.Stop();
            SaveSession();
            _sessionFp = null;
            _pendingSeek = null;
            _restoreNote = "";
            SavedText.Text = "";

            _media = mi;
            LayoutColumns();
            _settings.LastFolder = Path.GetDirectoryName(path) ?? "";
            TimelineView.Media = mi;
            TimelineView.LosslessRanges = null;
            _curSeg = -1; _lastShown = -1;
            _project.Reset(mi.Duration);
            TimelineView.ResetView();
            TimelineView.SetPlayhead(0, false);

            Session? restored = null;
            string? fp = null;
            if (SessionsEnabled)
            {
                try { fp = await Task.Run(() => SessionStore.Fingerprint(path)); }
                catch (Exception ex) { App.WriteLog("fingerprint: " + ex); }
                if (_media != mi) return;   // da mo file khac trong luc doi
                if (fp != null) restored = TryRestore(fp, path, mi);
            }

            _player!.Load(path);
            ApplyVolume();

            Title = "Video Cutter — " + Path.GetFileName(path);
            FileTitle.Text = Path.GetFileName(path);
            long size = 0;
            try { size = new System.IO.FileInfo(path).Length; } catch { }
            FileInfo.Text = $"{mi.Describe()} • {Fmt.Duration(mi.Duration)} • {FormatSize(size)}";
            SetAutoOutput(true);
            if (restored != null && !string.IsNullOrEmpty(restored.Output)) OutputBox.Text = restored.Output;
            _sessionFp = fp;
            Status("Đang đọc danh sách khung hình / keyframe… (vẫn xem và cắt được ngay)" + _restoreNote);
            KeyLegend.Text = "keyframe (đang đọc…)";

            var cts = new CancellationTokenSource();
            _framesCts = cts;
            var sw = Stopwatch.StartNew();
            _framesTask = Task.Run(() => mi.LoadFramesAsync(cts.Token));
            try
            {
                await _framesTask;
                if (cts.IsCancellationRequested || _media != mi) return;
                int kf = mi.Keyframes.Length;
                double gop = kf > 1 ? mi.Duration / kf : mi.Duration;
                KeyLegend.Text = mi.HasVideo ? $"keyframe ({kf.ToString("N0", Vi)} • ~{gop.ToString("0.#", Vi)} s/lần)" : "keyframe";
                Status(($"Sẵn sàng • {mi.FrameTimes.Length:N0} khung hình, {kf:N0} keyframe (đọc trong {sw.Elapsed.TotalSeconds:0.0} s). Nhấn F1 để xem phím tắt.".Replace(',', '.')) + _restoreNote);
                OnProjectChanged();
                UpdateModeHint();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.WriteLog("frames: " + ex);
                if (_media == mi) Status("Không đọc được danh sách khung hình (" + ex.Message + "). Cắt vẫn dùng được theo fps danh nghĩa.");
            }
        }

        private static string FormatSize(long b)
        {
            if (b <= 0) return "";
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.00", Vi) + " GB";
            return (b / (double)(1L << 20)).ToString("0.0", Vi) + " MB";
        }

        // =====================================================================================
        // Phien lam viec: tu luu / khoi phuc (gan voi dung video qua dau van tay)
        // =====================================================================================

        private Session? TryRestore(string fp, string path, MediaInfo mi)
        {
            var s = SessionStore.Load(fp);
            if (s == null || s.Segments == null || s.Segments.Count == 0)
            {
                var stale = SessionStore.StaleFor(path, fp);
                if (stale != null)
                    _restoreNote = $" • Có phiên cũ của file cùng tên nhưng video đã thay đổi (lưu {LocalTime(stale.SavedUtc)}) — không áp dụng để tránh lệch.";
                return null;
            }
            if (Math.Abs(s.Duration - mi.Duration) > 0.05)
            {
                _restoreNote = " • Phiên đã lưu không khớp độ dài video — không áp dụng.";
                return null;
            }
            _restoring = true;
            bool ok;
            try
            {
                ok = _project.Restore(s.Segments.Select(x => new Segment { Start = x.Start, End = x.End, Keep = x.Keep, Order = x.Order }));
            }
            finally { _restoring = false; }
            if (!ok) { _restoreNote = " • Phiên đã lưu bị hỏng — bắt đầu mới."; return null; }
            _pendingSeek = Math.Max(0, Math.Min(mi.Duration, s.Playhead));
            TimelineView.SetPlayhead(_pendingSeek.Value, false);
            _restoreNote = $" • Đã khôi phục phiên làm việc lưu lúc {LocalTime(s.SavedUtc)} ({s.Count} đoạn, giữ {LongDuration(s.KeptDuration)}). Ctrl+Z để bắt đầu lại từ video nguyên vẹn.";
            SavedText.Text = "• đã khôi phục phiên " + LocalTime(s.SavedUtc);
            App.WriteLog("session restored: " + s.Count + " segments, playhead " + s.Playhead.ToString(CultureInfo.InvariantCulture));
            return s;
        }

        private static string LocalTime(DateTime utc)
        {
            var t = utc.ToLocalTime();
            return t.Date == DateTime.Today ? t.ToString("HH:mm") + " hôm nay" : t.ToString("HH:mm dd/MM/yyyy");
        }

        private void ScheduleSave()
        {
            if (_sessionFp == null || _restoring || !SessionsEnabled) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Luu ngay trang thai hien tai (doan, thu tu, vi tri dang xem, ten file xuat tu dat).</summary>
        private void SaveSession()
        {
            if (!SessionsEnabled || _media == null || _sessionFp == null) return;
            try
            {
                var segs = _project.Segments;
                string? output = !string.IsNullOrEmpty(OutputBox.Text) && OutputBox.Text != _autoOutput ? OutputBox.Text : null;
                // chua cat gi: khong can luu (xoa phien cu neu co)
                if (segs.Count == 1 && segs[0].Keep && output == null)
                {
                    SessionStore.Delete(_sessionFp);
                    SavedText.Text = "";
                    return;
                }
                long size = 0;
                try { size = new System.IO.FileInfo(_media.Path).Length; } catch { }
                SessionStore.Save(new Session
                {
                    Fingerprint = _sessionFp,
                    VideoPath = _media.Path,
                    VideoSize = size,
                    Duration = _media.Duration,
                    Segments = segs.Select(s => new SessionSeg { Start = s.Start, End = s.End, Keep = s.Keep, Order = s.Order }).ToList(),
                    Playhead = Cur,
                    Output = output,
                    SavedUtc = DateTime.UtcNow,
                });
                SavedText.Text = "• đã lưu " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception ex)
            {
                App.WriteLog("session save: " + ex);
                SavedText.Text = "• không lưu được phiên";
            }
        }

        private void ShowRecent()
        {
            if (!SessionsEnabled) return;
            var items = SessionStore.All().Take(8).Select(s => new RecentItem
            {
                Path = s.VideoPath,
                Fingerprint = s.Fingerprint,
                Name = s.VideoName,
                Detail = $"{s.Count} đoạn • giữ {LongDuration(s.KeptDuration)} / {LongDuration(s.Duration)} • lưu {LocalTime(s.SavedUtc)}"
                         + (File.Exists(s.VideoPath) ? "" : " • không thấy file"),
            }).ToList();
            RecentList.ItemsSource = items;
            RecentPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Recent_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is string path)) return;
            if (File.Exists(path)) _ = OpenFileAsync(path);
            else Status("Không thấy video: " + path + " — nếu đã chuyển chỗ, hãy mở file ở vị trí mới: phiên vẫn tự khôi phục (nhận ra đúng video).");
        }

        private void RecentDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is string fp) || fp.Length == 0) return;
            var name = (fe.DataContext as RecentItem)?.Name ?? "";
            SessionStore.Delete(fp);
            ShowRecent();
            Status($"Đã xoá phiên làm việc của {name} khỏi lịch sử (file video không bị ảnh hưởng).");
        }

        private void RecentClearAll_Click(object sender, RoutedEventArgs e)
        {
            int n = SessionStore.All().Count;
            if (n == 0) { ShowRecent(); return; }
            if (MessageBox.Show(this, $"Xoá toàn bộ {n} phiên làm việc đã lưu?\nĐiểm cắt của các video sẽ mất; các file video không bị ảnh hưởng.",
                    "Video Cutter", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            SessionStore.DeleteAll();
            ShowRecent();
            Status($"Đã xoá {n} phiên làm việc.");
        }

        private void SetAutoOutput(bool force)
        {
            if (_media == null) return;
            if (!force && !string.IsNullOrEmpty(OutputBox.Text) && OutputBox.Text != _autoOutput)
            {
                // nguoi dung tu dat ten: chi doi duoi file cho hop che do
                string want = ExportPlanner.OutputExtension(_media, CurrentMode);
                try { OutputBox.Text = Path.ChangeExtension(OutputBox.Text, want); } catch { }
                return;
            }
            _autoOutput = ExportPlanner.DefaultOutputPath(_media, CurrentMode);
            OutputBox.Text = _autoOutput;
        }

        private void Open_Click(object sender, RoutedEventArgs e) => ShowOpenDialog();

        private void ShowOpenDialog()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Mở video",
                Filter = "Video|" + string.Join(";", VideoExts.Select(x => "*" + x)) + "|Mọi file|*.*",
            };
            if (Directory.Exists(_settings.LastFolder)) dlg.InitialDirectory = _settings.LastFolder;
            if (dlg.ShowDialog(this) == true) _ = OpenFileAsync(dlg.FileName);
            FocusSink.Focus();
        }

        private void OnWpfDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) _ = OpenFileAsync(f[0]);
        }

        // =====================================================================================
        // Vong cap nhat (30 Hz)
        // =====================================================================================

        private double Cur => _player?.TimePos ?? 0;
        private double Dur => _media?.Duration ?? 0;

        private void OnTick()
        {
            if (_player == null || _media == null) return;
            double t = _player.TimePos;

            if (!_player.Paused && (PreviewToggle.IsChecked == true || _joinCheck) && !_player.Seeking && Environment.TickCount - _lastSkipTick > 150)
                t = FollowOutputOrder(t);
            UpdatePosition(false, t);
        }

        /// <summary>
        /// Xem thu / xem cho noi: phat cac doan giu theo dung thu tu xuat (co the khac thu tu thoi gian),
        /// het doan nay thi nhay sang dau doan ke tiep.
        /// </summary>
        private double FollowOutputOrder(double t)
        {
            var r = _project.KeptRanges();
            if (r.Count == 0) { _player!.SetPause(true); Status("Không có đoạn nào được giữ."); return t; }
            double fd = _media!.FrameDuration;
            int k = _playItem;
            // nguoi dung tua di cho khac -> tim doan dang chua dau phat
            if (k < 0 || k >= r.Count || t < r[k].Start - 0.05 || t > r[k].End + 0.3)
                k = r.FindIndex(x => t >= x.Start - 1e-3 && t < x.End - fd * 0.5);
            if (k < 0)
            {
                // dang o doan bo -> sang doan giu ke tiep theo thoi gian
                var next = r.Select((x, i) => (x, i)).Where(p => p.x.Start > t).OrderBy(p => p.x.Start).FirstOrDefault();
                if (next.x == null) return StopPlayback("Hết các đoạn giữ.", t);
                return JumpTo(next.i, r);
            }
            _playItem = k;
            if (_stopAt.HasValue && k == _stopItem && t >= _stopAt.Value)
            {
                _joinCheck = false; _stopAt = null;
                return StopPlayback("Đã xem xong chỗ nối. Nhấn C để xem lại, ↑/↓ để tới điểm cắt.", t);
            }
            if (t >= r[k].End - fd * 0.5)
            {
                if (k + 1 >= r.Count) return StopPlayback("Hết các đoạn giữ.", t);
                return JumpTo(k + 1, r);
            }
            return t;
        }

        private double JumpTo(int k, List<Segment> r)
        {
            _playItem = k;
            _lastSkipTick = Environment.TickCount;
            _player!.SeekExact(r[k].Start);
            return r[k].Start;
        }

        private double StopPlayback(string msg, double t)
        {
            _player!.SetPause(true);
            _stopAt = null; _joinCheck = false;
            Status(msg);
            return t;
        }

        private void UpdatePosition(bool force, double? tOpt = null)
        {
            if (_player == null || _media == null) return;
            double t = tOpt ?? _player.TimePos;
            bool paused = _player.Paused;
            if (!force && Math.Abs(t - _lastShown) < 1e-6 && paused == _lastPaused) return;
            _lastShown = t; _lastPaused = paused;

            if (!TimeBox.IsKeyboardFocused) TimeBox.Text = Fmt.Clock(t);
            int idx = _media.FrameIndexAt(t);
            int total = _media.FrameTimes.Length > 0 ? _media.FrameTimes.Length : (int)Math.Round(_media.Duration * (_media.Fps > 0 ? _media.Fps : 30));
            string kf = _media.FramesReady && _media.HasVideo && _media.IsKeyframe(_media.SnapToFrame(t)) ? "  • keyframe" : "";
            FrameText.Text = _media.HasVideo ? $"khung {idx.ToString("N0", Vi)} / {total.ToString("N0", Vi)}{kf}" : "";
            TimelineView.SetPlayhead(t, !paused);

            PlayBtn.Content = paused && _shuttle >= 0 ? "\uE768" : "\uE769";
            SpeedText.Text = _shuttle < 0 ? $"◀×{-_shuttle}" : (!paused && Math.Abs(_player.Speed - 1) > 0.01 ? $"×{_player.Speed:0.#}" : "");

            int seg = _project.IndexAt(t);
            if (seg != _curSeg)
            {
                _curSeg = seg;
                TimelineView.SelectedIndex = seg;
                _suppressList = true;
                int pos = _project.PosOf(seg);
                SegmentList.SelectedIndex = pos;
                if (pos >= 0 && pos < _items.Count) SegmentList.ScrollIntoView(_items[pos]);
                _suppressList = false;
            }
        }

        // =====================================================================================
        // Dieu khien phat
        // =====================================================================================

        private void TogglePlay()
        {
            if (_player == null || _media == null) return;
            if (_shuttle != 0) { StopShuttle(); _player.SetPause(true); return; }
            if (_player.Paused)
            {
                _player.SetSpeed(1);
                if (Cur >= Dur - 0.05)
                {
                    var kr = _project.KeptRanges();
                    double start = PreviewToggle.IsChecked == true && kr.Count > 0 ? kr[0].Start : 0;
                    _playItem = 0;
                    _player.SeekExact(start);
                }
                else if (PreviewToggle.IsChecked == true)
                {
                    var kr = _project.KeptRanges();
                    if (kr.Count > 0 && _playItem == kr.Count - 1 && Cur >= kr[kr.Count - 1].End - 2 * _media.FrameDuration)
                    { _playItem = 0; _player.SeekExact(kr[0].Start); }
                }
                _player.SetPause(false);
            }
            else
            {
                _player.SetPause(true);
                _stopAt = null; _joinCheck = false;
            }
        }

        private void Pause()
        {
            StopShuttle();
            _stopAt = null; _joinCheck = false;
            _player?.SetPause(true);
        }

        private void StopShuttle()
        {
            if (_shuttle == 0) return;
            _reverse.Stop();
            if (_shuttle > 0) _player?.SetSpeed(1);
            _shuttle = 0;
        }

        private void ShuttleForward()
        {
            if (_player == null) return;
            if (_shuttle < 0) { _reverse.Stop(); _shuttle = 0; }
            _shuttle = _shuttle <= 0 ? 1 : Math.Min(8, _shuttle * 2);
            _player.SetSpeed(_shuttle);
            _player.SetPause(false);
            UpdatePosition(true);
        }

        private void ShuttleReverse()
        {
            if (_player == null) return;
            if (_shuttle > 0) { _player.SetSpeed(1); _shuttle = 0; }
            _shuttle = _shuttle >= 0 ? -1 : Math.Max(-8, _shuttle * 2);
            _player.SetPause(true);
            _reverse.Start();
            UpdatePosition(true);
        }

        private void OnReverseTick()
        {
            if (_player == null || _shuttle >= 0) { _reverse.Stop(); return; }
            if (_player.Seeking) return; // doi lan tua truoc xong
            double t = Cur - (-_shuttle) * _reverse.Interval.TotalSeconds;
            if (t <= 0) { t = 0; StopShuttle(); }
            _player.SeekExact(t);
            UpdatePosition(true, t);
        }

        private void SeekTo(double t)
        {
            if (_player == null || _media == null) return;
            t = Math.Max(0, Math.Min(Dur, t));
            _player.SeekExact(t);
            UpdatePosition(true, t);
        }

        private void StepFrames(int n)
        {
            if (_player == null || _media == null) return;
            StopShuttle();
            _player.SetPause(true);
            if (_media.FrameTimes.Length > 0)
            {
                int i = Math.Max(0, Math.Min(_media.FrameTimes.Length - 1, _media.FrameIndexAt(Cur) + n));
                SeekTo(_media.FrameTime(i));
            }
            else if (n > 0) _player.FrameStep();
            else _player.FrameBackStep();
        }

        private List<double> NavPoints()
        {
            var p = new List<double> { 0 };
            p.AddRange(_project.CutPoints());
            p.Add(Dur);
            return p;
        }

        /// <summary>Vi tri dung de tim diem cat ke tiep: neu dang xem khung cuoi truoc diem cat dang chon thi tinh tu diem cat do.</summary>
        private double NavCur()
        {
            double c = Cur;
            int sc = TimelineView.SelectedCut;
            if (_media != null && sc > 0 && sc < _project.Segments.Count)
            {
                double ct = _project.Segments[sc].Start;
                if (Math.Abs(ct - c) <= _media.FrameDuration * 1.5) c = ct;
            }
            return c;
        }

        private void GoNextCut()
        {
            double c = NavCur();
            var n = NavPoints().Where(x => x > c + 1e-4).DefaultIfEmpty(Dur).Min();
            Pause(); SelectCutAt(n); SeekTo(n);
        }

        private void GoPrevCut()
        {
            double c = NavCur();
            var p = NavPoints().Where(x => x < c - 1e-4).DefaultIfEmpty(0).Max();
            Pause(); SelectCutAt(p); SeekTo(p);
        }

        private void SelectCutAt(double t)
        {
            var segs = _project.Segments;
            int found = -1;
            for (int i = 1; i < segs.Count; i++) if (Math.Abs(segs[i].Start - t) < 1e-6) { found = i; break; }
            TimelineView.SelectedCut = found;
            TimelineView.InvalidateVisual();
            if (found > 0) Status("Đã chọn điểm cắt (vàng). Nhấn , hoặc . để dịch từng khung (Shift: 10 khung), hoặc kéo trên timeline.");
        }

        /// <summary>
        /// Bam giu / keo / dich diem cat: hien dung khung hinh o do. Diem cat ket thuc doan giu -> hien khung cuoi
        /// cung con giu; con lai -> hien khung dau tien sau diem cat.
        /// </summary>
        private void OnCutPreview(int cut, double t, bool final)
        {
            if (_player == null || _media == null) return;
            var segs = _project.Segments;
            if (cut <= 0 || cut >= segs.Count) return;
            if (!_player.Paused || _shuttle != 0) Pause();
            _stopAt = null; _joinCheck = false;

            bool endOfKept = segs[cut - 1].Keep && !segs[cut].Keep;
            int fi = _media.FrameIndexAt(t);
            int showIdx = endOfKept ? Math.Max(0, fi - 1) : fi;
            double show = endOfKept ? _media.FrameTime(showIdx) : t;
            _player.SeekExact(show);
            UpdatePosition(true, show);

            string what = endOfKept
                ? $"khung cuối còn giữ của đoạn {_project.PosOf(cut - 1) + 1}"
                : segs[cut].Keep ? $"khung đầu tiên của đoạn {_project.PosOf(cut) + 1}" : $"khung đầu tiên bị bỏ (đoạn {_project.PosOf(cut) + 1})";
            Status($"Điểm cắt {Fmt.Clock(t)} • đang xem {what} (khung {showIdx.ToString("N0", Vi)})" +
                   (final ? ". Nhấn , . để dịch từng khung (Shift: 10 khung)." : ""));
        }

        /// <summary>Phim , . : dich diem cat dang chon tung khung (Shift: 10 khung), hinh chay theo.</summary>
        private void NudgeCut(int frames)
        {
            if (_media == null) return;
            var segs = _project.Segments;
            int c = TimelineView.SelectedCut;
            if (c <= 0 || c >= segs.Count)
            {
                Status("Chưa chọn điểm cắt: bấm giữ một vạch cắt trên timeline hoặc dùng ↑ / ↓ để tới điểm cắt, rồi nhấn , . để dịch.");
                return;
            }
            double t = segs[c].Start;
            int fi = _media.FrameIndexAt(t) + frames;
            double nt = _media.FrameTime(Math.Max(1, fi));
            _project.BeginMoveCut();
            nt = _project.MoveCut(c, nt);
            _project.EndMoveCut();
            if (c < _project.Segments.Count) { TimelineView.SelectedCut = c; OnCutPreview(c, nt, true); }
        }

        private void GoKeyframe(int dir)
        {
            if (_media == null) return;
            if (!_media.FramesReady) { Status("Đang đọc keyframe, thử lại sau giây lát…"); return; }
            Pause();
            SeekTo(dir < 0 ? _media.PrevKeyframe(Cur) : _media.NextKeyframe(Cur));
        }

        private void Play_Click(object sender, RoutedEventArgs e) { TogglePlay(); FocusSink.Focus(); }
        private void FrameBack_Click(object sender, RoutedEventArgs e) { StepFrames(-1); FocusSink.Focus(); }
        private void FrameFwd_Click(object sender, RoutedEventArgs e) { StepFrames(1); FocusSink.Focus(); }
        private void PrevCut_Click(object sender, RoutedEventArgs e) { GoPrevCut(); FocusSink.Focus(); }
        private void NextCut_Click(object sender, RoutedEventArgs e) { GoNextCut(); FocusSink.Focus(); }

        // ---------------------------------------------------------------- am luong

        private void ApplyVolume()
        {
            if (_player == null) return;
            _player.SetVolume(VolumeSlider.Value);
            _player.SetMute(_muted);
        }

        private void UpdateMuteIcon()
        {
            MuteBtn.Content = _muted || VolumeSlider.Value < 0.5 ? "\uE74F" : "\uE767";
        }

        private void Mute_Click(object sender, RoutedEventArgs e) { ToggleMute(); FocusSink.Focus(); }

        private void ToggleMute()
        {
            _muted = !_muted;
            ApplyVolume();
            UpdateMuteIcon();
            Status(_muted ? "Đã tắt tiếng (M để bật lại)." : "Đã bật tiếng.");
        }

        private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            if (_muted && e.NewValue > e.OldValue) _muted = false;
            ApplyVolume();
            UpdateMuteIcon();
        }

        // ---------------------------------------------------------------- o thoi gian

        private void TimeBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => TimeBox.Dispatcher.BeginInvoke(new Action(TimeBox.SelectAll));
        private void TimeBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdatePosition(true);

        private void TimeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { FocusSink.Focus(); e.Handled = true; return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (_media == null) { FocusSink.Focus(); return; }
            string s = TimeBox.Text.Trim();
            if (s.StartsWith("#"))
            {
                if (int.TryParse(s.Substring(1).Replace(".", "").Replace(",", ""), out int fi)) { Pause(); SeekTo(_media.FrameTime(fi)); }
                else Status("Số khung không hợp lệ. Ví dụ: #1234");
            }
            else if (Fmt.TryParse(s, out double t)) { Pause(); SeekTo(t); }
            else Status("Thời gian không hợp lệ. Ví dụ: 1:23.5 hoặc 00:12:03.250 hoặc 95");
            FocusSink.Focus();
        }

        // =====================================================================================
        // Cat
        // =====================================================================================

        /// <summary>Dau khung dang hien (diem cat dat truoc khung nay).</summary>
        private double CutHere() => _media == null ? Cur : _media.SnapToFrame(Cur);

        /// <summary>Cuoi khung dang hien (dung cho O: giu ca khung dang xem).</summary>
        private double CutAfterCurrentFrame()
        {
            if (_media == null) return Cur;
            if (Cur >= Dur - 1e-6) return Dur;
            return _media.FrameTime(_media.FrameIndexAt(Cur) + 1);
        }

        private bool HasFile()
        {
            if (_media != null) return true;
            Status("Hãy mở video trước (Ctrl+O hoặc kéo thả file vào cửa sổ).");
            return false;
        }

        private void DoMarkIn()
        {
            if (!HasFile()) return;
            double t = CutHere();
            if (_project.MarkIn(t)) Status($"Giữ từ {Fmt.Time(t, _media)} — phần trước đó trong đoạn này bị bỏ.");
            else Status("Không đặt được điểm cắt ở đây.");
        }

        private void DoMarkOut()
        {
            if (!HasFile()) return;
            double t = CutAfterCurrentFrame();
            if (_project.MarkOut(t)) Status($"Giữ đến hết khung đang xem ({Fmt.Clock(t)}) — phần sau đó trong đoạn này bị bỏ.");
            else Status("Không đặt được điểm cắt ở đây.");
        }

        private void DoSplit()
        {
            if (!HasFile()) return;
            double t = CutHere();
            if (_project.Split(t)) Status($"Đã chia đoạn tại {Fmt.Time(t, _media)}.");
            else Status("Đầu phát đang ở ngay điểm cắt / mép đoạn — không chia thêm.");
        }

        private void DoToggle()
        {
            if (!HasFile()) return;
            int i = _project.IndexAt(Cur);
            _project.ToggleKeep(i);
            int no = _project.PosOf(i) + 1;
            Status(_project.Segments[i].Keep ? $"Đoạn {no}: giữ." : $"Đoạn {no}: bỏ.");
        }

        private void MarkIn_Click(object sender, RoutedEventArgs e) { DoMarkIn(); FocusSink.Focus(); }
        private void MarkOut_Click(object sender, RoutedEventArgs e) { DoMarkOut(); FocusSink.Focus(); }
        private void Split_Click(object sender, RoutedEventArgs e) { DoSplit(); FocusSink.Focus(); }
        private void Toggle_Click(object sender, RoutedEventArgs e) { DoToggle(); FocusSink.Focus(); }

        private void ClearCuts_Click(object sender, RoutedEventArgs e)
        {
            if (!HasFile()) return;
            if (_project.Segments.Count <= 1 && _project.Segments[0].Keep) return;
            _project.ClearAll();
            Status("Đã xoá hết điểm cắt (Ctrl+Z để hoàn tác).");
            FocusSink.Focus();
        }

        private void Undo_Click(object sender, RoutedEventArgs e) { DoUndo(); FocusSink.Focus(); }
        private void Redo_Click(object sender, RoutedEventArgs e) { DoRedo(); FocusSink.Focus(); }
        private void DoUndo() { if (_project.CanUndo) { _project.Undo(); Status("Đã hoàn tác."); } }
        private void DoRedo() { if (_project.CanRedo) { _project.Redo(); Status("Đã làm lại."); } }

        private void UpdateUndoButtons()
        {
            UndoBtn.IsEnabled = _project.CanUndo;
            RedoBtn.IsEnabled = _project.CanRedo;
        }

        // ---------------------------------------------------------------- xem thu / cho noi

        private void Preview_Click(object sender, RoutedEventArgs e) { SetPreview(PreviewToggle.IsChecked == true); FocusSink.Focus(); }

        private void SetPreview(bool on)
        {
            PreviewToggle.IsChecked = on;
            if (_player == null || _media == null) return;
            if (on)
            {
                StopShuttle();
                var r = _project.KeptRanges();
                if (r.Count == 0) { PreviewToggle.IsChecked = false; Status("Không có đoạn nào được giữ."); return; }
                double fd = _media.FrameDuration, c = Cur;
                int k = r.FindIndex(x => c >= x.Start - 1e-3 && c < x.End - fd);
                if (k < 0) { k = 0; _player.SeekExact(r[0].Start); }
                _playItem = k;
                _player.SetSpeed(1);
                _player.SetPause(false);
                string ord = _project.IsReordered ? " theo thứ tự đã sắp xếp" : "";
                Status($"Xem thử kết quả: phát liền các đoạn giữ{ord} ({Fmt.Duration(_project.KeptDuration)}), tự nhảy qua đoạn bỏ. Nhấn P để tắt.");
            }
            else Status("Đã tắt xem thử.");
        }

        private void CheckJoin_Click(object sender, RoutedEventArgs e) { CheckJoin(); FocusSink.Focus(); }

        private void CheckJoin()
        {
            if (!HasFile() || _player == null) return;
            var r = _project.KeptRanges();
            if (r.Count < 2) { Status("Chưa có chỗ nối nào (cần ít nhất 2 đoạn giữ cách nhau bởi đoạn bỏ)."); return; }
            double c = Cur;
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < r.Count - 1; i++)
            {
                double d = Math.Min(Math.Abs(c - r[i].End), Math.Abs(c - r[i + 1].Start));
                if (d < bestD) { bestD = d; best = i; }
            }
            double from = Math.Max(r[best].Start, r[best].End - 2);
            double to = Math.Min(r[best + 1].End, r[best + 1].Start + 2);
            StopShuttle();
            _player.SetSpeed(1);
            _player.SeekExact(from);
            _playItem = best;
            _lastSkipTick = Environment.TickCount;
            _joinCheck = true;
            _stopAt = to;
            _stopItem = best + 1;
            _player.SetPause(false);
            Status($"Xem chỗ nối {best + 1}: {Fmt.Clock(r[best].End)} ⟶ {Fmt.Clock(r[best + 1].Start)} (2 giây trước và sau).");
        }

        // =====================================================================================
        // Danh sach doan
        // =====================================================================================

        private void OnProjectChanged()
        {
            UpdateUndoButtons();
            ScheduleSave();
            if (_media == null) { _items.Clear(); KeptSummary.Text = "Chưa mở video"; TimelineView.InvalidateVisual(); return; }

            bool lossless = CurrentMode == ExportMode.Lossless && _media.FramesReady && _media.HasVideo;
            var kept = _project.KeptRanges();
            TimelineView.LosslessRanges = lossless && kept.Count > 0 ? ExportPlanner.LosslessRanges(_media, kept) : null;

            var keepSoft = (Brush)FindResource("KeepSoftBrush");
            var keepBr = (Brush)FindResource("KeepBrush");
            var text = (Brush)FindResource("TextBrush");
            var muted = (Brush)FindResource("MutedBrush");
            var dropBg = (Brush)FindResource("DropBrush");

            var segs = _project.Segments;
            if (TimelineView.SelectedCut >= segs.Count) TimelineView.SelectedCut = -1;
            _suppressList = true;
            // danh sach theo thu tu xuat; cap nhat tai cho (giu vi tri cuon) neu so luong khong doi
            var list = new List<SegItem>();
            double dur = Math.Max(1e-6, _media.Duration);
            double outAt = 0;   // vi tri trong video xuat
            for (int pos = 0; pos < segs.Count; pos++)
            {
                int i = _project.SegmentAtPos(pos);
                var s = segs[i];
                string where = s.Keep
                    ? $" • xuất {LongDuration(outAt)} → {LongDuration(outAt + s.Length)}"
                    : " • bỏ";
                if (s.Keep) outAt += s.Length;
                list.Add(new SegItem
                {
                    Index = i,
                    Pos = pos,
                    Number = (pos + 1).ToString(CultureInfo.InvariantCulture),
                    Badge = s.Keep ? keepSoft : dropBg,
                    BadgeText = s.Keep ? keepBr : muted,
                    Range = $"{ShortClock(s.Start)} → {ShortClock(s.End)}",
                    RangeBrush = s.Keep ? text : muted,
                    Length = LongDuration(s.Length),
                    LengthBrush = s.Keep ? keepBr : muted,
                    Detail = FrameCount(s) + where,
                    Keep = s.Keep,
                    CanUp = _project.CanMoveUp(pos),
                    CanDown = _project.CanMoveDown(pos),
                    CanMerge = _project.CanMergeUp(pos),
                    CanDelete = _project.CanDelete(i),
                    PreW = new GridLength(Math.Max(0, s.Start / dur), GridUnitType.Star),
                    LenW = new GridLength(Math.Max(1e-4, s.Length / dur), GridUnitType.Star),
                    PostW = new GridLength(Math.Max(0, (dur - s.End) / dur), GridUnitType.Star),
                    BarBrush = s.Keep ? keepBr : dropBg,
                });
            }
            if (_items.Count == list.Count) for (int i = 0; i < list.Count; i++) _items[i] = list[i];
            else { _items.Clear(); foreach (var it in list) _items.Add(it); }
            _curSeg = -1;
            _suppressList = false;

            int keptN = segs.Count(s => s.Keep);
            double keptDur = _project.KeptDuration;
            string extra = "";
            if (TimelineView.LosslessRanges != null)
            {
                double actual = TimelineView.LosslessRanges.Sum(x => x.ActualEnd - x.ActualStart);
                double more = actual - keptDur;
                if (more > 0.05) extra = $" • dư {Fmt.Duration(more)} do keyframe";
            }
            KeptSummary.Text = keptN == 0
                ? "Không có đoạn nào được giữ"
                : $"{segs.Count} đoạn • {keptN} giữ, {segs.Count - keptN} bỏ";

            // Tong phan giu lai
            TotalText.Text = keptN == 0 ? "0:00" : LongDuration(keptDur);
            int joins = Math.Max(0, kept.Count - 1);
            var parts = new List<string>
            {
                $"{keptN} đoạn • {(_media.Duration > 0 ? keptDur / _media.Duration * 100 : 0):0}% video gốc",
                $"bỏ {LongDuration(_media.Duration - keptDur)}",
            };
            if (joins > 0) parts.Add($"{joins} chỗ nối");
            if (_project.IsReordered) parts.Add("đã đổi thứ tự");
            TotalDetail.Text = string.Join(" • ", parts) + extra;
            ResetOrderBtn.Visibility = _project.IsReordered ? Visibility.Visible : Visibility.Collapsed;

            ExportBtn.IsEnabled = keptN > 0;
            TimelineView.InvalidateVisual();
            UpdatePosition(true);
        }

        /// <summary>Do dai kem phan muoi giay: 12:34,5 / 1:02:03,4</summary>
        private static string LongDuration(double t)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, t) + 1e-7);
            string s = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
            return s + "," + (ts.Milliseconds / 100);
        }

        private void MergeUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is int pos && _project.MergeUp(pos))
                Status($"Đã gộp đoạn {pos + 1} vào đoạn {pos} (Ctrl+Z để hoàn tác).");
            FocusSink.Focus();
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is int pos && _project.CanMoveUp(pos))
            {
                _project.MoveUp(pos);
                _suppressList = true;
                SegmentList.SelectedIndex = pos - 1;
                _suppressList = false;
                Status($"Đã chuyển đoạn lên vị trí {pos}. Video xuất sẽ theo thứ tự trong danh sách (P để xem thử).");
            }
            FocusSink.Focus();
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is int pos && _project.CanMoveDown(pos))
            {
                _project.MoveDown(pos);
                _suppressList = true;
                SegmentList.SelectedIndex = pos + 1;
                _suppressList = false;
                Status($"Đã chuyển đoạn xuống vị trí {pos + 2}. Video xuất sẽ theo thứ tự trong danh sách (P để xem thử).");
            }
            FocusSink.Focus();
        }

        private void DeleteSeg_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is int i) DeleteSegment(i);
            FocusSink.Focus();
        }

        private void DeleteSegment(int i)
        {
            if (i < 0 || i >= _project.Segments.Count) return;
            int no = _project.PosOf(i) + 1;
            string range = $"{ShortClock(_project.Segments[i].Start)} → {ShortClock(_project.Segments[i].End)}";
            if (_project.DeleteSegment(i)) Status($"Đã xoá đoạn {no} ({range}) khỏi video xuất. Ctrl+Z để hoàn tác.");
            else Status("Đoạn này đã bỏ rồi.");
        }

        private void ResetOrder_Click(object sender, RoutedEventArgs e)
        {
            _project.ResetOrder();
            Status("Đã xếp các đoạn về đúng thứ tự thời gian.");
            FocusSink.Focus();
        }

        /// <summary>Bo phan gio khi video ngan hon 1 gio (danh sach doan hep).</summary>
        private string ShortClock(double t)
        {
            string c = Fmt.Clock(t);
            return Dur < 3600 && c.StartsWith("00:") ? c.Substring(3) : c;
        }

        private string FrameCount(Segment s)
        {
            if (_media == null || !_media.HasVideo || _media.FrameTimes.Length == 0) return "";
            int a = _media.FrameIndexAt(s.Start);
            int b = s.End >= _media.Duration - 1e-6 ? _media.FrameTimes.Length : _media.FrameIndexAt(s.End);
            return $" • {Math.Max(0, b - a).ToString("N0", Vi)} khung";
        }

        private void SegmentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressList || _media == null) return;
            int i = _project.SegmentAtPos(SegmentList.SelectedIndex);
            if (i < 0 || i >= _project.Segments.Count) return;
            Pause();
            SeekTo(_project.Segments[i].Start);
        }

        private void SegmentList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && (FindParent<CheckBox>(d) != null || FindParent<Button>(d) != null)) return;
            int i = _project.SegmentAtPos(SegmentList.SelectedIndex);
            if (i >= 0) ZoomToSegment(i);
            e.Handled = true;
        }

        private void ZoomToSegment(int i)
        {
            if (i < 0 || i >= _project.Segments.Count) return;
            var s = _project.Segments[i];
            TimelineView.ZoomToRange(s.Start, s.End);
        }

        private void KeepCheck_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is int i) _project.ToggleKeep(i);
            FocusSink.Focus();
        }

        private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        // =====================================================================================
        // Timeline zoom
        // =====================================================================================

        private void ZoomIn_Click(object sender, RoutedEventArgs e) { TimelineView.Zoom(0.5); FocusSink.Focus(); }
        private void ZoomOut_Click(object sender, RoutedEventArgs e) { TimelineView.Zoom(2); FocusSink.Focus(); }
        private void ZoomFit_Click(object sender, RoutedEventArgs e) { TimelineView.ResetView(); FocusSink.Focus(); }

        // =====================================================================================
        // Phim tat
        // =====================================================================================

        private void Shortcuts_Click(object sender, RoutedEventArgs e) => ShortcutsPopup.IsOpen = !ShortcutsPopup.IsOpen;

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mod = Keyboard.Modifiers;
            bool ctrl = (mod & ModifierKeys.Control) != 0, shift = (mod & ModifierKeys.Shift) != 0, alt = (mod & ModifierKeys.Alt) != 0;

            if (Keyboard.FocusedElement is TextBox)
            {
                if (key == Key.Escape) { FocusSink.Focus(); e.Handled = true; }
                return; // de o nhap lieu tu xu ly
            }
            if (ShortcutsPopup.IsOpen && (key == Key.Escape || key == Key.F1)) { ShortcutsPopup.IsOpen = false; e.Handled = true; return; }

            bool handled = true;
            // phim lap lai khi giu: bo qua neu lan tua truoc chua xong (tranh don lenh)
            bool busy = e.IsRepeat && _player != null && _player.Seeking;
            switch (key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.K: Pause(); _player?.SetSpeed(1); break;
                case Key.L: ShuttleForward(); break;
                case Key.J: ShuttleReverse(); break;
                case Key.Left:
                    if (busy) break;
                    if (alt) GoKeyframe(-1); else if (ctrl) { SeekTo(Cur - 10); } else if (shift) { SeekTo(Cur - 1); } else StepFrames(-1);
                    break;
                case Key.Right:
                    if (busy) break;
                    if (alt) GoKeyframe(1); else if (ctrl) { SeekTo(Cur + 10); } else if (shift) { SeekTo(Cur + 1); } else StepFrames(1);
                    break;
                case Key.Up: if (!busy) GoPrevCut(); break;
                case Key.Down: if (!busy) GoNextCut(); break;
                case Key.Home: Pause(); SeekTo(0); break;
                case Key.End: Pause(); SeekTo(Dur); break;
                case Key.I: DoMarkIn(); break;
                case Key.O:
                    if (ctrl) ShowOpenDialog(); else DoMarkOut();
                    break;
                case Key.S: if (!ctrl) DoSplit(); else handled = false; break;
                case Key.B: if (ctrl) DoSplit(); else handled = false; break;
                case Key.Delete:
                    if (shift) { if (HasFile()) DeleteSegment(_project.IndexAt(Cur)); }
                    else DoToggle();
                    break;
                case Key.Back: DoToggle(); break;
                case Key.P: SetPreview(PreviewToggle.IsChecked != true); break;
                case Key.C: if (!ctrl) CheckJoin(); else handled = false; break;
                case Key.M: ToggleMute(); break;
                case Key.Z: if (ctrl) { if (shift) DoRedo(); else DoUndo(); } else handled = false; break;
                case Key.Y: if (ctrl) DoRedo(); else handled = false; break;
                case Key.E: if (ctrl) Export_Click(this, new RoutedEventArgs()); else handled = false; break;
                case Key.OemPlus:
                case Key.Add: TimelineView.Zoom(0.5); break;
                case Key.OemMinus:
                case Key.Subtract: TimelineView.Zoom(2); break;
                case Key.D0:
                case Key.NumPad0: if (ctrl) TimelineView.ResetView(); else handled = false; break;
                case Key.F1: ShortcutsPopup.IsOpen = !ShortcutsPopup.IsOpen; break;
                case Key.OemComma: NudgeCut(shift ? -10 : -1); break;
                case Key.OemPeriod: NudgeCut(shift ? 10 : 1); break;
                case Key.Escape: Pause(); SetPreviewOff(); TimelineView.SelectedCut = -1; TimelineView.InvalidateVisual(); break;
                default: handled = false; break;
            }
            if (handled)
            {
                e.Handled = true;
                if (!(Keyboard.FocusedElement is TextBox) && !FocusSink.IsKeyboardFocused && key != Key.O) FocusSink.Focus();
            }
        }

        private void SetPreviewOff()
        {
            if (PreviewToggle.IsChecked == true) { PreviewToggle.IsChecked = false; Status("Đã tắt xem thử."); }
        }

        // =====================================================================================
        // Xuat
        // =====================================================================================

        private ExportMode CurrentMode =>
            ModeLossless.IsChecked == true ? ExportMode.Lossless : ModeSmall.IsChecked == true ? ExportMode.ExactSmall : ExportMode.Exact;

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            SetAutoOutput(false);
            OnProjectChanged();
            UpdateModeHint();
            SaveSettings();
        }

        private void UpdateModeHint()
        {
            if (!_ready) return;
            var mode = CurrentMode;
            if (mode == ExportMode.Lossless)
            {
                if (_media == null) { ModeHint.Text = "Phần tô vàng trên timeline là phần dư do bắt vào keyframe."; return; }
                if (!_media.FramesReady) { ModeHint.Text = "Đang đọc keyframe…"; return; }
                int kf = _media.Keyframes.Length;
                double gop = kf > 1 ? _media.Duration / kf : _media.Duration;
                ModeHint.Text = $"Không mã hoá lại, chất lượng 100% gốc. Keyframe cách nhau ~{gop:0.#} s → đầu/cuối mỗi đoạn có thể dư tối đa ~{gop:0.#} s (phần tô vàng trên timeline).";
                return;
            }
            var t = Encoders.AvailableAsync();
            string enc;
            if (!t.IsCompleted) enc = "đang dò bộ mã hoá GPU…";
            else
            {
                var a = t.Result;
                bool hevc = _media?.VideoCodec == "hevc";
                string fam = hevc ? "hevc" : "h264";
                enc = a.Contains(fam + "_qsv") ? "Intel Quick Sync (GPU)"
                    : a.Contains(fam + "_nvenc") ? "NVIDIA NVENC (GPU)"
                    : a.Contains(fam + "_amf") ? "AMD AMF (GPU)"
                    : (hevc ? "x265 (CPU)" : "x264 (CPU)");
                if (_settings.Encoder == "cpu") enc = hevc ? "x265 (CPU)" : "x264 (CPU)";
            }
            ModeHint.Text = mode == ExportMode.Exact
                ? $"Chính xác từng khung, chỗ nối liền mạch, giữ độ phân giải & fps gốc. Mã hoá bằng {enc}, chất lượng gần như gốc."
                : $"Chính xác từng khung, nén mạnh hơn — file nhỏ để gửi đi, chữ vẫn rõ. Mã hoá bằng {enc}.";
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            if (!HasFile()) return;
            string ext = ExportPlanner.OutputExtension(_media!, CurrentMode);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Lưu video đã cắt",
                Filter = $"Video (*{ext})|*{ext}",
                DefaultExt = ext,
                AddExtension = true,
                OverwritePrompt = false,
            };
            try
            {
                dlg.InitialDirectory = Path.GetDirectoryName(OutputBox.Text);
                dlg.FileName = Path.GetFileName(OutputBox.Text);
            }
            catch { }
            if (dlg.ShowDialog(this) == true) OutputBox.Text = dlg.FileName;
            FocusSink.Focus();
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_exportCts != null || !HasFile()) return;
            var mi = _media!;
            var kept = _project.KeptRanges();
            if (kept.Count == 0) { Status("Chưa có đoạn nào được giữ."); return; }

            string output = OutputBox.Text.Trim();
            try { output = Path.GetFullPath(output); }
            catch { MessageBox.Show(this, "Đường dẫn file xuất không hợp lệ.", "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            string want = ExportPlanner.OutputExtension(mi, CurrentMode);
            if (!string.Equals(Path.GetExtension(output), want, StringComparison.OrdinalIgnoreCase)) output = Path.ChangeExtension(output, want);
            if (string.Equals(output, Path.GetFullPath(mi.Path), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Không thể ghi đè lên chính video gốc. Hãy chọn tên khác.", "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string? dir = Path.GetDirectoryName(output);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "Thư mục lưu không tồn tại:\n" + dir, "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (File.Exists(output) &&
                MessageBox.Show(this, "File đã tồn tại. Ghi đè?\n" + output, "Video Cutter", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            OutputBox.Text = output;

            Pause();
            SetPreviewOff();
            var mode = CurrentMode;
            var cts = new CancellationTokenSource();
            _exportCts = cts;
            ExportBtn.Visibility = Visibility.Collapsed;
            ExportProgressPanel.Visibility = Visibility.Visible;
            ExportDonePanel.Visibility = Visibility.Collapsed;
            ExportProgress.Value = 0;
            ExportStatus.Text = "Đang chuẩn bị…";
            ModeExact.IsEnabled = ModeLossless.IsEnabled = ModeSmall.IsEnabled = false;

            try
            {
                if (mode == ExportMode.Lossless && mi.HasVideo && !mi.FramesReady && _framesTask != null)
                {
                    ExportStatus.Text = "Đang đọc keyframe…";
                    try { await _framesTask; } catch { }
                    if (!mi.FramesReady) throw new InvalidOperationException("Chưa đọc được keyframe của video — hãy dùng chế độ “Chính xác từng khung”.");
                }

                var opt = new ExportOptions { Mode = mode, Encoder = _settings.Encoder, OutputPath = output };
                var plan = await ExportPlanner.BuildAsync(mi, kept, opt);
                App.WriteLog($"Xuất {plan.Summary} -> {output}");
                var sw = Stopwatch.StartNew();
                string modeName = mode == ExportMode.Lossless ? "Đang copy" : "Đang mã hoá";
                ExportStatus.Text = $"{modeName}… 0%";

                var res = await Exporter.RunAsync(plan, output, p => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_exportCts != cts) return;
                    ExportProgress.Value = p;
                    string eta = "";
                    if (p > 0.03 && p < 1)
                    {
                        double left = sw.Elapsed.TotalSeconds * (1 - p) / p;
                        eta = left >= 60 ? $" • còn ~{(int)(left / 60)} phút {(int)(left % 60)} giây" : $" • còn ~{Math.Ceiling(left)} giây";
                    }
                    ExportStatus.Text = $"{modeName}… {p * 100:0}%{eta}";
                })), l => App.WriteLog("  " + l), cts.Token);

                if (res.Ok)
                {
                    _lastResult = res.File;
                    long size = 0;
                    try { size = new System.IO.FileInfo(res.File).Length; } catch { }
                    ExportDoneText.Text = $"Xong trong {FormatElapsed(res.Elapsed)} • {FormatSize(size)}";
                    ExportDonePanel.Visibility = Visibility.Visible;
                    Status($"Đã xuất: {res.File} ({Fmt.Duration(plan.OutputDuration)}, {plan.Summary}).");
                    SetAutoOutput(true); // lan xuat sau khong ghi de
                }
                else if (res.Cancelled) Status("Đã huỷ xuất video.");
                else
                {
                    Status("Xuất video thất bại. Xem chi tiết trong VideoCutter.log.");
                    MessageBox.Show(this, "Xuất video thất bại:\n\n" + res.Error, "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                App.WriteLog("export: " + ex);
                MessageBox.Show(this, ex.Message, "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _exportCts = null;
                ExportBtn.Visibility = Visibility.Visible;
                ExportProgressPanel.Visibility = Visibility.Collapsed;
                ModeExact.IsEnabled = ModeLossless.IsEnabled = ModeSmall.IsEnabled = true;
                FocusSink.Focus();
            }
        }

        private static string FormatElapsed(TimeSpan t) =>
            t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} phút {t.Seconds} giây" : $"{t.TotalSeconds:0.0} giây".Replace('.', ',');

        private void CancelExport_Click(object sender, RoutedEventArgs e) { _exportCts?.Cancel(); ExportStatus.Text = "Đang huỷ…"; }

        private void OpenResult_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResult == null || !File.Exists(_lastResult)) return;
            try { Process.Start(new ProcessStartInfo(_lastResult) { UseShellExecute = true }); } catch (Exception ex) { Status(ex.Message); }
        }

        private void OpenResultFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResult == null) return;
            try { Process.Start("explorer.exe", "/select,\"" + _lastResult + "\""); } catch (Exception ex) { Status(ex.Message); }
        }

        // =====================================================================================
        // Tien ich
        // =====================================================================================

        private void Status(string s) => StatusText.Text = s;

        /// <summary>--snapshot: dung vai doan mau, chup giao dien (va khung video tu mpv) roi thoat.</summary>
        private async Task SnapshotAndExitAsync(string path)
        {
            try
            {
                if (_media != null)
                {
                    for (int i = 0; i < 100 && !_media.FramesReady; i++) await Task.Delay(100);
                    double d = _media.Duration;
                    SeekTo(d * 0.12); await Task.Delay(300); DoMarkIn();
                    SeekTo(d * 0.40); await Task.Delay(300); DoMarkOut();
                    SeekTo(d * 0.55); await Task.Delay(300); DoMarkIn();
                    SeekTo(d * 0.80); await Task.Delay(300); DoSplit();
                    _project.MoveUp(_project.Segments.Count - 1);
                    TimelineView.SelectedCut = 1; OnCutPreview(1, _project.Segments[1].Start, true);
                    await Task.Delay(800);
                    _player?.Command("screenshot-to-file", Path.ChangeExtension(path, null) + "_video.png", "video");
                    await Task.Delay(700);
                }
                await Task.Delay(500);
                var root = (FrameworkElement)Content;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                var rtb = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                var bg = new System.Windows.Shapes.Rectangle { Width = root.ActualWidth, Height = root.ActualHeight, Fill = Background };
                bg.Measure(new Size(root.ActualWidth, root.ActualHeight));
                bg.Arrange(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                rtb.Render(bg);
                rtb.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
            }
            catch (Exception ex) { App.WriteLog("snapshot: " + ex); }
            Close();
        }
    }

    /// <summary>Cai dat luu canh exe (key=value, de doc va sua tay).</summary>
    public sealed class Settings
    {
        public ExportMode Mode = ExportMode.Exact;
        public double Volume = 100;
        public bool Muted;
        public string LastFolder = "";
        /// <summary>auto | gpu | cpu</summary>
        public string Encoder = "auto";

        private static string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VideoCutter.settings");

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "mode": if (Enum.TryParse(v, true, out ExportMode m)) s.Mode = m; break;
                        case "volume": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var vol)) s.Volume = vol; break;
                        case "muted": s.Muted = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "lastFolder": s.LastFolder = v; break;
                        case "encoder": if (v == "auto" || v == "gpu" || v == "cpu") s.Encoder = v; break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new[]
                {
                    "# Video Cutter - cai dat (encoder: auto | gpu | cpu)",
                    "mode=" + Mode,
                    "volume=" + Volume.ToString("0", CultureInfo.InvariantCulture),
                    "muted=" + (Muted ? "1" : "0"),
                    "lastFolder=" + LastFolder,
                    "encoder=" + Encoder,
                });
            }
            catch { }
        }
    }
}
