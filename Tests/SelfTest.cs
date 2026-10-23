using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VideoCutterApp.Editing;
using VideoCutterApp.Export;
using VideoCutterApp.Media;

namespace VideoCutterApp.Tests
{
    /// <summary>
    /// Kiem tra tu dong (VideoCutter.exe --selftest [thu_muc]):
    /// tao video thu co so thu tu khung "in" vao do sang (2 o vuong) + tieng bip dung moi giay,
    /// cat o cac khung KHONG phai keyframe, xuat bang moi che do, roi doc lai tung khung de kiem tra
    /// dung khung, khong khung, timestamp lien tuc, tieng khop hinh.
    /// </summary>
    public static class SelfTest
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const int Fps = 30;
        private const int TotalFrames = 2400; // 80 giay

        private static StringBuilder _log = new StringBuilder();
        private static int _fail, _ok;

        private static void Log(string s) { _log.AppendLine(s); Console.WriteLine(s); }
        private static void Check(string name, bool cond, string detail = "")
        {
            if (cond) _ok++; else _fail++;
            Log($"  [{(cond ? "OK" : "FAIL")}] {name} {detail}");
        }

        public static async Task<int> RunAsync(string? dir, string? realVideo)
        {
            dir ??= Path.Combine(Path.GetTempPath(), "videocutter_selftest");
            Directory.CreateDirectory(dir);
            Log($"Video Cutter self-test • {DateTime.Now:yyyy-MM-dd HH:mm:ss} • {dir}");
            ProjectTests();

            var sources = new (string Name, string Args)[]
            {
                ("mp4_gop10s_bframes.mp4", "-c:v libx264 -g 300 -keyint_min 300 -sc_threshold 0 -bf 2 -pix_fmt yuv420p -c:a aac -b:a 128k"),
                ("mkv_gop2s.mkv",          "-c:v libx264 -g 60 -keyint_min 60 -sc_threshold 0 -bf 0 -pix_fmt yuv420p -c:a aac -b:a 128k"),
                ("mp4_start1.4s.mp4",      "-c:v libx264 -g 90 -keyint_min 90 -sc_threshold 0 -bf 2 -pix_fmt yuv420p -c:a aac -b:a 128k -output_ts_offset 1.4"),
            };
            // Doan giu (theo so khung): bat dau/ket thuc deu khong phai keyframe; doan cuoi chay toi het video
            var keepFrames = new (int A, int B)[] { (47, 213), (400, 777), (1201, 1650), (2100, TotalFrames) };

            foreach (var (name, encArgs) in sources)
            {
                string src = Path.Combine(dir, name);
                if (!File.Exists(src))
                {
                    string vf = "color=c=black:s=640x360:r=30:d=80,format=yuv420p,geq=lum='if(lt(X\\,320)\\,20+mod(N\\,50)*4\\,20+mod(floor(N/50)\\,50)*4)':cb=128:cr=128";
                    string af = "aevalsrc='if(lt(mod(t\\,1)\\,0.1)\\,0.8*sin(2*PI*1000*t)\\,0)':s=48000:d=80";
                    var g = await Tools.RunAsync(Tools.FFmpeg, $"-hide_banner -loglevel error -y -f lavfi -i \"{vf}\" -f lavfi -i \"{af}\" {encArgs} -shortest {Tools.Quote(src)}");
                    if (g.Exit != 0) { Log("Không tạo được video thử: " + g.Err); return 2; }
                }
                var mi = await MediaInfo.ProbeAsync(src);
                await mi.LoadFramesAsync();
                Log($"\n=== {name}: {mi.Describe()} • {mi.Duration:0.000}s • start {mi.StartTime:0.###}s • {mi.FrameTimes.Length} khung • {mi.Keyframes.Length} keyframe");
                Check("Đọc đủ khung hình", mi.FrameTimes.Length == TotalFrames, $"({mi.FrameTimes.Length})");

                var kept = keepFrames.Select(k => new Segment { Start = mi.FrameTime(k.A), End = k.B >= TotalFrames ? mi.Duration : mi.FrameTime(k.B), Keep = true }).ToList();

                foreach (var (mode, enc) in new[] { (ExportMode.Lossless, "auto"), (ExportMode.Exact, "auto"), (ExportMode.ExactSmall, "cpu") })
                {
                    string ext = ExportPlanner.OutputExtension(mi, mode);
                    string outp = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "__" + mode + "_" + enc + ext);
                    var opt = new ExportOptions { Mode = mode, Encoder = enc, OutputPath = outp };
                    var plan = await ExportPlanner.BuildAsync(mi, kept, opt);
                    var res = await Exporter.RunAsync(plan, outp, null, null, default);
                    Log($"  -- {plan.Summary}: {(res.Ok ? "xong" : "LỖI " + res.Error)} ({res.Elapsed.TotalSeconds:0.0}s)");
                    if (!res.Ok) { _fail++; continue; }
                    await Analyze(mi, outp, mode, plan, keepFrames);
                }

                // Doi thu tu xuat (doan 3, 1, 4, 2): van dung tung khung, khong dung hinh
                if (name != "mp4_start1.4s.mp4")
                {
                    var order = new[] { 2, 0, 3, 1 };
                    var keepR = order.Select(i => keepFrames[i]).ToArray();
                    var keptR = order.Select(i => kept[i]).ToList();
                    foreach (var mode in new[] { ExportMode.Exact, ExportMode.Lossless })
                    {
                        string ext = ExportPlanner.OutputExtension(mi, mode);
                        string outp = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "__reorder_" + mode + ext);
                        var plan = await ExportPlanner.BuildAsync(mi, keptR, new ExportOptions { Mode = mode, OutputPath = outp });
                        var res = await Exporter.RunAsync(plan, outp, null, null, default);
                        Log($"  -- Đổi thứ tự 3-1-4-2 • {plan.Summary}: {(res.Ok ? "xong" : "LỖI " + res.Error)} ({res.Elapsed.TotalSeconds:0.0}s)");
                        if (!res.Ok) { _fail++; continue; }
                        await Analyze(mi, outp, mode, plan, keepR);
                    }
                }
            }

            if (realVideo != null && File.Exists(realVideo)) await RealVideo(realVideo, dir);

            Log($"\nKết quả: {_ok} OK, {_fail} FAIL");
            File.WriteAllText(Path.Combine(dir, "selftest.log"), _log.ToString(), new UTF8Encoding(false));
            return _fail == 0 ? 0 : 1;
        }

        private sealed class Frame { public double T; public int N; }

        private static string JoinTimes(List<double> j) => string.Join(", ", j.Select(x => x.ToString("0.000", Inv)));

        /// <summary>Kiem tra logic doan: chia, doi thu tu, gop, hoan tac.</summary>
        private static void ProjectTests()
        {
            Log("\n=== Danh sách đoạn (thứ tự / gộp)");
            var p = new Project();
            p.Reset(100);
            p.Split(30); p.Split(60);                    // [0-30][30-60][60-100]
            string R() => string.Join(" ", p.KeptRanges().Select(s => $"{s.Start:0}-{s.End:0}"));
            Check("Chia: 1 đoạn liền (gộp khi xuất)", R() == "0-100", R());
            p.MoveUp(2);                                  // danh sach: [0-30][60-100][30-60]
            Check("Chuyển lên: thứ tự xuất 0-30, 60-100, 30-60", R() == "0-30 60-100 30-60" && p.IsReordered, R());
            Check("Gộp: không cho gộp 2 đoạn không liền nhau", !p.CanMergeUp(1) && p.CanMergeUp(2));
            p.MergeUp(2);                                 // 60-100 + 30-60 -> 30-100 o vi tri 2
            Check("Gộp với đoạn trên (liền nhau)", p.Segments.Count == 2 && R() == "0-100", $"{p.Segments.Count} đoạn: {R()}");
            p.Undo(); p.Undo();
            Check("Hoàn tác về thứ tự gốc", p.Segments.Count == 3 && !p.IsReordered, R());
            p.MoveUp(2); p.ToggleKeep(p.SegmentAtPos(0));  // bo 0-30
            p.Split(80);                                  // chia doan 60-100 -> 60-80, 80-100 van dung cho
            Check("Chia sau khi đổi thứ tự giữ đúng chỗ", R() == "60-100 30-60", R());
            p.ResetOrder();
            Check("Về thứ tự gốc", R() == "30-100" && !p.IsReordered, R());
            // [0-30 bo][30-60][60-80][80-100]
            p.DeleteSegment(1);
            Check("Xoá đoạn: gộp vào phần bỏ bên cạnh", p.Segments.Count == 3 && R() == "60-100", $"{p.Segments.Count} đoạn: {R()}");
            p.DeleteSegment(2);
            Check("Xoá đoạn cuối", p.Segments.Count == 3 && R() == "60-80", $"{p.Segments.Count} đoạn: {R()}");
            Check("Không xoá được đoạn đã bỏ nằm giữa đoạn giữ", !p.CanDelete(2) && !p.DeleteSegment(2));
            p.DeleteSegment(1);
            Check("Xoá đoạn cuối cùng còn giữ: còn 1 phần bỏ", p.Segments.Count == 1 && R() == "", $"{p.Segments.Count} đoạn");
            p.Undo(); p.Undo(); p.Undo();
            Check("Hoàn tác xoá", p.Segments.Count == 4 && R() == "30-100", $"{p.Segments.Count} đoạn: {R()}");

            Log("\n=== Phiên làm việc (tự lưu / khôi phục)");
            string dir = Path.Combine(Path.GetTempPath(), "vc_session_test_" + Guid.NewGuid().ToString("N"));
            string? old = SessionStore.DirOverride;
            SessionStore.DirOverride = Path.Combine(dir, "sessions");
            try
            {
                Directory.CreateDirectory(dir);
                string vid = Path.Combine(dir, "a.bin");
                var rnd = new Random(1); var data = new byte[3 << 20]; rnd.NextBytes(data);
                File.WriteAllBytes(vid, data);
                string fp1 = SessionStore.Fingerprint(vid), fp2 = SessionStore.Fingerprint(vid);
                Check("Dấu vân tay ổn định", fp1 == fp2, fp1);
                string moved = Path.Combine(dir, "doi_ten.bin"); File.Copy(vid, moved);
                Check("Đổi tên / chép chỗ khác vẫn nhận ra", SessionStore.Fingerprint(moved) == fp1);
                data[data.Length - 10] ^= 0xFF; File.WriteAllBytes(moved, data);
                Check("Video bị thay đổi -> dấu vân tay khác", SessionStore.Fingerprint(moved) != fp1);

                SessionStore.Save(new Session
                {
                    Fingerprint = fp1, VideoPath = vid, Duration = 100, Playhead = 42.5, SavedUtc = DateTime.UtcNow,
                    Segments = p.Segments.Select(s => new SessionSeg { Start = s.Start, End = s.End, Keep = s.Keep, Order = s.Order }).ToList(),
                });
                var back = SessionStore.Load(fp1);
                var q = new Project(); q.Reset(100);
                bool ok = back != null && back.Segments != null &&
                          q.Restore(back.Segments.Select(x => new Segment { Start = x.Start, End = x.End, Keep = x.Keep, Order = x.Order }));
                string RQ() => string.Join(" ", q.KeptRanges().Select(s => $"{s.Start:0}-{s.End:0}"));
                Check("Lưu rồi mở lại: đúng các đoạn + vị trí xem", ok && q.Segments.Count == p.Segments.Count && RQ() == R() && Math.Abs(back!.Playhead - 42.5) < 1e-9, RQ());
                q.Undo();
                Check("Ctrl+Z sau khôi phục: về video nguyên vẹn", q.Segments.Count == 1 && q.Segments[0].Keep);
                Check("Phiên cũ của file cùng tên bị thay đổi được phát hiện", SessionStore.StaleFor(vid, "khac") != null);
                Check("Từ chối phiên không phủ kín video", !q.Restore(new[] { new Segment { Start = 0, End = 50 } }));
                SessionStore.Delete(fp1);
                Check("Xoá phiên", SessionStore.Load(fp1) == null);
            }
            finally
            {
                SessionStore.DirOverride = old;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static async Task<List<Frame>> ReadFrames(string file)
        {
            async Task<List<(double t, double y)>> Region(int x)
            {
                var list = new List<(double, double)>();
                var r = await Tools.RunAsync(Tools.FFmpeg, $"-hide_banner -nostats -i {Tools.Quote(file)} -map 0:v:0 -vf crop=200:200:{x}:80,showinfo -fps_mode passthrough -f null -");
                foreach (Match m in Regex.Matches(r.Err, @"pts_time:\s*([-0-9.]+).*?mean:\[(\d+)"))
                    list.Add((double.Parse(m.Groups[1].Value, Inv), double.Parse(m.Groups[2].Value, Inv)));
                return list;
            }
            var lo = await Region(60);
            var hi = await Region(380);
            var frames = new List<Frame>();
            for (int i = 0; i < Math.Min(lo.Count, hi.Count); i++)
            {
                int l = (int)Math.Round((lo[i].y - 20) / 4.0), h = (int)Math.Round((hi[i].y - 20) / 4.0);
                frames.Add(new Frame { T = lo[i].t, N = h * 50 + l });
            }
            return frames;
        }

        private static async Task Analyze(MediaInfo src, string outp, ExportMode mode, ExportPlan plan, (int A, int B)[] keep)
        {
            var frames = await ReadFrames(outp);
            var info = await MediaInfo.ProbeAsync(outp);
            var ns = frames.Select(f => f.N).ToList();

            // 1. Dung khung
            if (mode != ExportMode.Lossless)
            {
                var expected = keep.SelectMany(k => Enumerable.Range(k.A, k.B - k.A)).ToList();
                bool same = expected.SequenceEqual(ns);
                int firstDiff = Enumerable.Range(0, Math.Min(expected.Count, ns.Count)).FirstOrDefault(i => expected[i] != ns[i]);
                Check("Đúng từng khung (đầu/cuối mỗi đoạn lệch 0 khung)", same,
                    same ? $"({ns.Count} khung)" : $"(cần {expected.Count} khung, có {ns.Count}; khác ở #{firstDiff}: cần {expected.ElementAtOrDefault(firstDiff)}, có {ns.ElementAtOrDefault(firstDiff)})");
            }
            else
            {
                // Lossless: moi doan thuc te phai chua tron doan da chon va bat dau dung keyframe
                var runs = new List<(int a, int b)>();
                foreach (var n in ns) { if (runs.Count > 0 && n == runs[runs.Count - 1].b) runs[runs.Count - 1] = (runs[runs.Count - 1].a, n + 1); else runs.Add((n, n + 1)); }
                bool cover = keep.All(k => runs.Any(r => r.a <= k.A && r.b >= k.B));
                bool startsKey = runs.All(r => src.IsKeyframe(src.FrameTime(r.a)));
                Check("Chứa trọn các đoạn đã chọn", cover, $"(đoạn xuất: {string.Join(", ", runs.Select(r => $"{r.a}-{r.b - 1}"))})");
                Check("Mỗi đoạn bắt đầu bằng keyframe (không có khung mồ côi)", startsKey);
            }

            // 2. Khong khung: khong co khung lap lai / dung hinh
            int dups = 0;
            for (int i = 1; i < ns.Count; i++) if (ns[i] == ns[i - 1]) dups++;
            Check("Không đứng hình (không khung lặp)", dups == 0, $"({dups} khung lặp)");

            // 3. Timestamp lien tuc
            double fd = 1.0 / Fps, maxGap = 0;
            for (int i = 1; i < frames.Count; i++) maxGap = Math.Max(maxGap, frames[i].T - frames[i - 1].T);
            bool mono = frames.Zip(frames.Skip(1), (a, b) => b.T > a.T).All(x => x);
            Check("Timestamp liên tục, không có lỗ", mono && maxGap < fd * 1.5, $"(khoảng cách lớn nhất {maxGap * 1000:0.0} ms)");

            // 4. Do dai
            double expectDur = plan.OutputDuration;
            // lossless: goi tieng AAC (21 ms) o cuoi moi doan + do tre B-frame -> cho phep lech vai khung
            Check("Độ dài đúng", Math.Abs(info.Duration - expectDur) < (mode == ExportMode.Lossless ? 0.15 : 0.1), $"({info.Duration:0.000}s, cần {expectDur:0.000}s)");

            // 5. Tieng khop hinh: moi tieng bip bat dau dung khung co so thu tu chia het cho 30
            var sil = await Tools.RunAsync(Tools.FFmpeg, $"-hide_banner -nostats -i {Tools.Quote(outp)} -map 0:a:0 -af silencedetect=n=-30dB:d=0.05 -f null -");
            var onsets = Regex.Matches(sil.Err, @"silence_end:\s*([0-9.]+)").Cast<Match>().Select(m => double.Parse(m.Groups[1].Value, Inv)).ToList();
            // moc noi trong file xuat
            var joins = new List<double>();
            for (int i = 1; i < frames.Count; i++) if (frames[i].N != frames[i - 1].N + 1) joins.Add(frames[i].T);
            double t0 = frames.Count > 0 ? frames[0].T : 0;
            var errs = new List<double>(); var errAt = new List<double>();
            foreach (var x in onsets)
            {
                if (joins.Any(j => x >= j - 0.02 && x < j + 0.15) || x < t0 + 0.15 || x > info.Duration - 0.15) continue; // bip bi cat do diem noi
                var f = frames.OrderBy(fr => Math.Abs(fr.T - x)).First();
                double srcT = f.N / (double)Fps + (x - f.T);
                errs.Add(srcT - Math.Round(srcT)); errAt.Add(x);
            }
            double worst = errs.Count > 0 ? errs.Max(e => Math.Abs(e)) : 99;
            Check("Tiếng khớp hình (lệch < 1 khung)", errs.Count >= 10 && worst < fd, $"({errs.Count} tiếng bíp, lệch lớn nhất {worst * 1000:0.0} ms" + (worst >= fd && errs.Count > 0 ? $" tại {errAt[errs.FindIndex(e => Math.Abs(e) == worst)]:0.000}s; nối tại {JoinTimes(joins)}" : "") + ")");
        }

        /// <summary>Thu tren ban sao video that: chi kiem tra khong khung + do dai + thoi gian xuat.</summary>
        private static async Task RealVideo(string path, string dir)
        {
            var mi = await MediaInfo.ProbeAsync(path);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await mi.LoadFramesAsync();
            Log($"\n=== Video thật: {Path.GetFileName(path)} • {mi.Describe()} • {mi.Duration:0}s • đọc {mi.FrameTimes.Length} khung trong {sw.Elapsed.TotalSeconds:0.0}s");
            // 3 doan, moi doan 20 s, diem cat lech keyframe
            var kept = new[] { 600.37, 1800.81, 3000.53 }.Select(t => new Segment { Start = mi.SnapToFrame(t), End = mi.SnapToFrame(t + 20), Keep = true }).ToList();
            foreach (var mode in new[] { ExportMode.Lossless, ExportMode.Exact })
            {
                string outp = Path.Combine(dir, "real__" + mode + ExportPlanner.OutputExtension(mi, mode));
                var plan = await ExportPlanner.BuildAsync(mi, kept, new ExportOptions { Mode = mode, OutputPath = outp });
                var res = await Exporter.RunAsync(plan, outp, null, null, default);
                Log($"  -- {plan.Summary}: {(res.Ok ? "xong" : "LỖI " + res.Error)} ({res.Elapsed.TotalSeconds:0.0}s)");
                if (!res.Ok) { _fail++; continue; }
                var info = await MediaInfo.ProbeAsync(outp);
                Check("Độ dài đúng", Math.Abs(info.Duration - plan.OutputDuration) < 0.15, $"({info.Duration:0.000}s, cần {plan.OutputDuration:0.000}s)");
                var fd = await Tools.RunAsync(Tools.FFmpeg, $"-hide_banner -nostats -i {Tools.Quote(outp)} -map 0:v:0 -vf \"select='not(mod(n\\,1))',showinfo\" -f null -");
                var ts = Regex.Matches(fd.Err, @"pts_time:\s*([-0-9.]+)").Cast<Match>().Select(m => double.Parse(m.Groups[1].Value, Inv)).ToList();
                double maxGap = 0; for (int i = 1; i < ts.Count; i++) maxGap = Math.Max(maxGap, ts[i] - ts[i - 1]);
                Check("Không có lỗ thời gian > 0.5 s (không khựng)", maxGap < 0.5, $"(lớn nhất {maxGap * 1000:0} ms, {ts.Count} khung)");
            }
        }
    }
}
