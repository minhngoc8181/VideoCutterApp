using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoCutterApp.Editing;
using VideoCutterApp.Media;

namespace VideoCutterApp.Export
{
    public enum ExportMode
    {
        /// <summary>Copy luong, cat tai keyframe: khong mat chat luong, vai giay.</summary>
        Lossless,
        /// <summary>Ma hoa lai 1 luot, chinh xac tung khung, chat luong gan nhu goc.</summary>
        Exact,
        /// <summary>Nhu Exact nhung nen manh hon.</summary>
        ExactSmall,
    }

    /// <summary>Do tim bo ma hoa GPU co tren may (Intel QSV / NVIDIA NVENC / AMD AMF).</summary>
    public static class Encoders
    {
        private static Task<HashSet<string>>? _probe;

        public static Task<HashSet<string>> AvailableAsync() => _probe ??= ProbeAsync();

        private static async Task<HashSet<string>> ProbeAsync()
        {
            var ok = new HashSet<string>();
            foreach (var enc in new[] { "h264_qsv", "h264_nvenc", "h264_amf", "hevc_qsv", "hevc_nvenc", "hevc_amf" })
            {
                try
                {
                    string args = $"-hide_banner -loglevel error -f lavfi -i color=c=gray:s=640x360:r=30:d=0.3 -c:v {enc} -f null -";
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var r = await Tools.RunAsync(Tools.FFmpeg, args, cts.Token).ConfigureAwait(false);
                    if (r.Exit == 0) ok.Add(enc);
                }
                catch { }
            }
            return ok;
        }
    }

    public sealed class ExportOptions
    {
        public ExportMode Mode = ExportMode.Exact;
        /// <summary>"auto" | "gpu" | "cpu"</summary>
        public string Encoder = "auto";
        public string OutputPath = "";
    }

    /// <summary>Mot doan se duoc xuat (thoi diem thuc te, sau khi bat dinh keyframe neu lossless).</summary>
    public sealed class PlannedRange
    {
        public double Start, End;          // doan nguoi dung chon
        public double ActualStart, ActualEnd; // doan thuc su xuat
    }

    public sealed class ExportPlan
    {
        public string Args = "";
        public List<PlannedRange> Ranges = new List<PlannedRange>();
        public double OutputDuration;
        public string? ConcatListPath;
        public string? ConcatListPath2;
        public string VideoEncoder = "";
        public string Summary = "";
    }

    public static class ExportPlanner
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static string F(double v) => v.ToString("0.000000", Inv);

        /// <summary>Lui diem cat 0.5 ms: khung hinh tai dung diem cat chac chan duoc tinh vao doan sau.</summary>
        private const double Eps = 0.0005;

        public static string DefaultOutputPath(MediaInfo mi, ExportMode mode)
        {
            string dir = Path.GetDirectoryName(mi.Path) ?? "";
            string name = Path.GetFileNameWithoutExtension(mi.Path);
            string ext = OutputExtension(mi, mode);
            string p = Path.Combine(dir, name + "_cut" + ext);
            for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{name}_cut{i}{ext}");
            return p;
        }

        /// <summary>Giu dinh dang goc. Ma hoa lai: mp4/mov/m4v -> mp4, con lai -> mkv (chua duoc moi codec).</summary>
        public static string OutputExtension(MediaInfo mi, ExportMode mode)
        {
            string ext = mi.Extension;
            if (mode == ExportMode.Lossless) return string.IsNullOrEmpty(ext) ? ".mkv" : ext;
            return ext == ".mp4" || ext == ".mov" || ext == ".m4v" ? ".mp4" : ".mkv";
        }

        public static async Task<ExportPlan> BuildAsync(MediaInfo mi, IList<Segment> kept, ExportOptions opt)
        {
            if (kept.Count == 0) throw new InvalidOperationException("Chưa có đoạn nào được giữ.");
            return opt.Mode == ExportMode.Lossless ? BuildLossless(mi, kept, opt) : await BuildExactAsync(mi, kept, opt).ConfigureAwait(false);
        }

        // ------------------------------------------------------------ lossless

        /// <summary>
        /// Copy nguyen luong. Diem dau doan bat dinh ve keyframe &lt;= diem cat, diem cuoi ve keyframe &gt;= diem cat
        /// (khong bao gio mat noi dung da chon). Moi doan bat dau bang keyframe nen khong co khung "mo coi"
        /// -> khong dung hinh o cho noi. Dung concat demuxer voi inpoint/outpoint tren file goc, 1 luot.
        /// </summary>
        /// <summary>Cac doan thuc su duoc copy khi xuat lossless (da bat dinh keyframe, gop doan chong nhau).</summary>
        public static List<PlannedRange> LosslessRanges(MediaInfo mi, IList<Segment> kept)
        {
            var ranges = new List<PlannedRange>();
            foreach (var s in kept)
            {
                double a = mi.HasVideo ? mi.KeyframeAtOrBefore(s.Start) : s.Start;
                double b = s.End >= mi.Duration - 1e-3 ? mi.Duration : (mi.HasVideo ? mi.KeyframeAtOrAfter(s.End) : s.End);
                // doan sau chong len doan truoc (2 diem cat cung trong 1 GOP, dung thu tu thoi gian) -> gop lai
                if (ranges.Count > 0 && a <= ranges[ranges.Count - 1].ActualEnd + 1e-6 && s.Start >= ranges[ranges.Count - 1].End - 1e-6)
                {
                    var last = ranges[ranges.Count - 1];
                    last.ActualEnd = Math.Max(last.ActualEnd, b);
                    last.End = s.End;
                    continue;
                }
                ranges.Add(new PlannedRange { Start = s.Start, End = s.End, ActualStart = a, ActualEnd = b });
            }
            return ranges;
        }

        public static ExportPlan BuildLossless(MediaInfo mi, IList<Segment> kept, ExportOptions opt)
        {
            var plan = new ExportPlan { VideoEncoder = "copy" };
            plan.Ranges = LosslessRanges(mi, kept);

            // Concat demuxer so outpoint voi DTS cua goi. Video co B-frame: keyframe K co DTS < PTS, nen
            // outpoint = PTS(K) se lot them K va vai khung P sau no (khung "lac" + hut khung o cho noi).
            // -> Hinh: outpoint = DTS(K) (dung truoc K theo thu tu giai ma, du moi khung hien thi truoc K).
            //    Tieng: outpoint = PTS(K). Ca hai dung "duration" giong nhau nen moc thoi gian cac doan khop nhau.
            bool bframes = mi.HasVideo && mi.Keyframes.Zip(mi.KeyframeDts, (p, d) => Math.Abs(p - d) > 1e-4).Any(x => x);
            string esc = mi.Path.Replace("\\", "/").Replace("'", "'\\''");
            string MakeList(bool videoList)
            {
                var sb = new StringBuilder("ffconcat version 1.0\n");
                for (int k = 0; k < plan.Ranges.Count; k++)
                {
                    var r = plan.Ranges[k];
                    bool last = k == plan.Ranges.Count - 1;
                    sb.Append("file '").Append(esc).Append("'\n");
                    // inpoint/outpoint tinh theo timestamp trong file -> cong lai start_time
                    if (r.ActualStart > 1e-6) sb.Append("inpoint ").Append(F(r.ActualStart + mi.StartTime)).Append('\n');
                    if (r.ActualEnd < mi.Duration - 1e-6)
                    {
                        double op = videoList && bframes ? mi.KeyframeDtsOf(r.ActualEnd) : r.ActualEnd;
                        sb.Append("outpoint ").Append(F(op + mi.StartTime)).Append('\n');
                        sb.Append("duration ").Append(F(r.ActualEnd - r.ActualStart)).Append('\n');
                    }
                    else if (!last)
                    {
                        // chay toi het file nhung con doan khac phia sau (da doi thu tu): chot tai cuoi hinh,
                        // neu khong phan tieng du / do dai file se day lech cac doan sau
                        double end = mi.VideoEnd;
                        if (!(videoList && bframes)) sb.Append("outpoint ").Append(F(end + mi.StartTime)).Append('\n');
                        sb.Append("duration ").Append(F(end - r.ActualStart)).Append('\n');
                    }
                }
                string path = Path.Combine(Path.GetTempPath(), "videocutter_" + Guid.NewGuid().ToString("N") + ".ffconcat");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            string list = MakeList(true);
            string? alist = bframes && mi.Audio.Count > 0 ? MakeList(false) : null;
            plan.ConcatListPath = list;
            plan.ConcatListPath2 = alist;

            var a2 = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "warning", "-nostats", "-progress", "pipe:1",
                "-f", "concat", "-safe", "0", "-i", Tools.Quote(list) };
            if (alist != null) a2.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", Tools.Quote(alist) });
            if (mi.HasVideo) a2.AddRange(new[] { "-map", "0:" + mi.VideoIndex.ToString(Inv) });
            foreach (var au in mi.Audio) a2.AddRange(new[] { "-map", (alist != null ? "1:" : "0:") + au.Index.ToString(Inv) });
            a2.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero", "-map_metadata", "-1", "-map_chapters", "-1" });
            if (IsMp4(opt.OutputPath)) a2.AddRange(new[] { "-movflags", "+faststart" });
            a2.Add(Tools.Quote(opt.OutputPath));
            plan.Args = string.Join(" ", a2);
            plan.OutputDuration = plan.Ranges.Sum(r => r.ActualEnd - r.ActualStart);
            plan.Summary = $"Giữ nguyên gốc (copy) • {plan.Ranges.Count} đoạn";
            return plan;
        }

        // ------------------------------------------------------------ exact (ma hoa lai)

        /// <summary>
        /// Moi doan la 1 input rieng (-ss/-to truoc -i: tua nhanh toi keyframe roi giai ma bo phan thua ->
        /// chinh xac tung khung), noi bang concat filter -> timestamp lien tuc, khong the khung.
        /// Am thanh cat cung luc voi hinh (chinh xac tung mau) nen khong lech dan.
        /// </summary>
        public static async Task<ExportPlan> BuildExactAsync(MediaInfo mi, IList<Segment> kept, ExportOptions opt)
        {
            var plan = new ExportPlan();
            foreach (var s in kept) plan.Ranges.Add(new PlannedRange { Start = s.Start, End = s.End, ActualStart = s.Start, ActualEnd = s.End });

            bool small = opt.Mode == ExportMode.ExactSmall;
            var avail = opt.Encoder == "cpu" ? new HashSet<string>() : await Encoders.AvailableAsync().ConfigureAwait(false);
            bool hevc = mi.VideoCodec == "hevc";
            string fam = hevc ? "hevc" : "h264";
            string? gpu = new[] { fam + "_qsv", fam + "_nvenc", fam + "_amf" }.FirstOrDefault(avail.Contains);
            bool tenBit = mi.PixFmt.Contains("10");

            var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "warning", "-nostats", "-progress", "pipe:1" };
            for (int k = 0; k < plan.Ranges.Count; k++)
            {
                var r = plan.Ranges[k];
                double ss = Math.Max(0, r.Start - Eps);
                a.AddRange(new[] { "-ss", F(ss) });
                if (r.End < mi.Duration - 1e-6) a.AddRange(new[] { "-to", F(r.End - Eps) });
                // doan chay toi het file nhung khong o cuoi (da doi thu tu): chot tai cuoi hinh de tieng khong du
                else if (k < plan.Ranges.Count - 1 && mi.HasVideo) a.AddRange(new[] { "-to", F(mi.VideoEnd - Eps) });
                a.AddRange(new[] { "-i", Tools.Quote(mi.Path) });
            }

            bool hasA = mi.Audio.Count > 0;
            int ai = hasA ? mi.Audio[0].Index : -1;
            var fc = new StringBuilder();
            for (int i = 0; i < plan.Ranges.Count; i++)
            {
                if (mi.HasVideo) fc.Append($"[{i}:{mi.VideoIndex}]");
                if (hasA) fc.Append($"[{i}:{ai}]");
            }
            fc.Append($"concat=n={plan.Ranges.Count}:v={(mi.HasVideo ? 1 : 0)}:a={(hasA ? 1 : 0)}");
            if (mi.HasVideo) fc.Append("[v]");
            if (hasA) fc.Append("[a]");
            a.AddRange(new[] { "-filter_complex", Tools.Quote(fc.ToString()) });
            if (mi.HasVideo) a.AddRange(new[] { "-map", "[v]" });
            if (hasA) a.AddRange(new[] { "-map", "[a]" });

            if (mi.HasVideo)
            {
                // Giu timestamp goc cua tung khung (dung voi ca video VFR nhu ban ghi man hinh)
                a.AddRange(new[] { "-fps_mode", "passthrough" });
                double fps = mi.Fps > 0 ? mi.Fps : 30;
                string gop = Math.Max(1, (int)Math.Round(fps * 4)).ToString(Inv);
                if (gpu != null && opt.Encoder != "cpu")
                {
                    plan.VideoEncoder = gpu;
                    a.AddRange(new[] { "-c:v", gpu });
                    if (gpu.EndsWith("_qsv")) a.AddRange(new[] { "-preset", "medium", "-global_quality", small ? "26" : "18" });
                    else if (gpu.EndsWith("_nvenc")) a.AddRange(new[] { "-preset", "p5", "-rc", "vbr", "-cq", small ? "28" : "19", "-b:v", "0" });
                    else a.AddRange(new[] { "-quality", "quality", "-rc", "cqp", "-qp_i", small ? "26" : "18", "-qp_p", small ? "28" : "20" });
                    if (tenBit && hevc) a.AddRange(new[] { "-pix_fmt", "p010le" }); else a.AddRange(new[] { "-pix_fmt", "nv12" });
                }
                else if (hevc)
                {
                    plan.VideoEncoder = "libx265";
                    a.AddRange(new[] { "-c:v", "libx265", "-preset", "medium", "-crf", small ? "26" : "20", "-tag:v", "hvc1",
                        "-pix_fmt", tenBit ? "yuv420p10le" : "yuv420p", "-x265-params", "log-level=error" });
                }
                else
                {
                    plan.VideoEncoder = "libx264";
                    a.AddRange(new[] { "-c:v", "libx264", "-preset", small ? "medium" : "fast", "-crf", small ? "23" : "18", "-pix_fmt", "yuv420p" });
                }
                a.AddRange(new[] { "-g", gop });
            }
            if (hasA)
            {
                var au = mi.Audio[0];
                long br = small ? 96_000 : Math.Max(128_000, Math.Min(320_000, au.BitRate > 0 ? au.BitRate : 192_000));
                a.AddRange(new[] { "-c:a", "aac", "-b:a", (br / 1000).ToString(Inv) + "k" });
                if (au.SampleRate > 0) a.AddRange(new[] { "-ar", au.SampleRate.ToString(Inv) });
            }
            a.AddRange(new[] { "-map_metadata", "-1", "-map_chapters", "-1" });
            if (IsMp4(opt.OutputPath)) a.AddRange(new[] { "-movflags", "+faststart" });
            a.Add(Tools.Quote(opt.OutputPath));

            plan.Args = string.Join(" ", a);
            plan.OutputDuration = plan.Ranges.Sum(r => r.ActualEnd - r.ActualStart);
            plan.Summary = $"{(small ? "Chính xác – file nhỏ" : "Chính xác – như gốc")} • {plan.ValueEncoderName()} • {plan.Ranges.Count} đoạn";
            return plan;
        }

        private static string ValueEncoderName(this ExportPlan p) => p.VideoEncoder switch
        {
            var e when e.EndsWith("_qsv") => "Intel QSV (GPU)",
            var e when e.EndsWith("_nvenc") => "NVIDIA NVENC (GPU)",
            var e when e.EndsWith("_amf") => "AMD AMF (GPU)",
            "libx265" => "x265 (CPU)",
            "libx264" => "x264 (CPU)",
            "" => "chỉ âm thanh",
            var e => e,
        };

        private static bool IsMp4(string path)
        {
            var e = Path.GetExtension(path).ToLowerInvariant();
            return e == ".mp4" || e == ".mov" || e == ".m4v" || e == ".m4a";
        }
    }

    public sealed class ExportResult
    {
        public bool Ok;
        public bool Cancelled;
        public string File = "";
        public string Error = "";
        public TimeSpan Elapsed;
    }

    /// <summary>Chay ffmpeg theo ke hoach: ghi ra file tam, xong moi doi ten. Bao tien do 0..1.</summary>
    public static class Exporter
    {
        public static async Task<ExportResult> RunAsync(ExportPlan plan, string outputPath, Action<double>? progress,
            Action<string>? log, CancellationToken ct)
        {
            var res = new ExportResult { File = outputPath };
            var sw = Stopwatch.StartNew();
            string ext = Path.GetExtension(outputPath);
            string tmp = Path.Combine(Path.GetDirectoryName(outputPath) ?? "", Path.GetFileNameWithoutExtension(outputPath) + ".dang-xuat" + ext);
            string args = plan.Args.Replace(Tools.Quote(outputPath), Tools.Quote(tmp));
            log?.Invoke("ffmpeg " + args);
            double total = Math.Max(0.001, plan.OutputDuration);
            var err = new StringBuilder();
            try
            {
                var r = await Tools.RunAsync(Tools.FFmpeg, args, ct,
                    onStderrLine: l => { if (err.Length < 20000) err.AppendLine(l); log?.Invoke(l); },
                    onStdoutLine: l =>
                    {
                        if (l.StartsWith("out_time_us=") && long.TryParse(l.Substring(12), out var us) && us > 0)
                            progress?.Invoke(Math.Min(1, us / 1e6 / total));
                    },
                    priority: ProcessPriorityClass.BelowNormal).ConfigureAwait(false);
                if (r.Exit != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length < 1024)
                {
                    res.Error = LastLines(err.ToString(), 6);
                    TryDelete(tmp);
                    return res;
                }
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tmp, outputPath);
                progress?.Invoke(1);
                res.Ok = true;
            }
            catch (OperationCanceledException)
            {
                res.Cancelled = true;
                await Task.Delay(300).ConfigureAwait(false);
                TryDelete(tmp);
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                TryDelete(tmp);
            }
            finally
            {
                if (plan.ConcatListPath != null) TryDelete(plan.ConcatListPath);
                if (plan.ConcatListPath2 != null) TryDelete(plan.ConcatListPath2);
                res.Elapsed = sw.Elapsed;
            }
            return res;
        }

        private static string LastLines(string s, int n)
        {
            var lines = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - n)));
        }

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    }
}
