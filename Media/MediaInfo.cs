using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoCutterApp.Media
{
    public static class Tools
    {
        public static string Dir => AppDomain.CurrentDomain.BaseDirectory;
        public static string FFmpeg => Path.Combine(Dir, "ffmpeg.exe");
        public static string FFprobe => Path.Combine(Dir, "ffprobe.exe");

        public static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        /// <summary>Chay mot tien trinh, tra ve (exit, stdout, stderr). Huy duoc.</summary>
        public static async Task<(int Exit, string Out, string Err)> RunAsync(string exe, string args, CancellationToken ct = default,
            Action<string>? onStderrLine = null, Action<string>? onStdoutLine = null, ProcessPriorityClass? priority = null)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var so = new StringBuilder(); var se = new StringBuilder();
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            p.OutputDataReceived += (s, e) => { if (e.Data == null) return; if (onStdoutLine != null) onStdoutLine(e.Data); else so.AppendLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data == null) return; se.AppendLine(e.Data); onStderrLine?.Invoke(e.Data); };
            p.Exited += (s, e) => tcs.TrySetResult(0);
            p.Start();
            if (priority != null) { try { p.PriorityClass = priority.Value; } catch { } }
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } }))
            {
                await tcs.Task.ConfigureAwait(false);
            }
            p.WaitForExit();
            ct.ThrowIfCancellationRequested();
            return (p.ExitCode, so.ToString(), se.ToString());
        }
    }

    [DataContract] internal class ProbeRoot { [DataMember(Name = "streams")] public List<ProbeStream>? Streams; [DataMember(Name = "format")] public ProbeFormat? Format; }
    [DataContract] internal class ProbeFormat
    {
        [DataMember(Name = "duration")] public string? Duration;
        [DataMember(Name = "start_time")] public string? StartTime;
        [DataMember(Name = "format_name")] public string? FormatName;
        [DataMember(Name = "bit_rate")] public string? BitRate;
    }
    [DataContract] internal class ProbeStream
    {
        [DataMember(Name = "index")] public int Index;
        [DataMember(Name = "codec_type")] public string? CodecType;
        [DataMember(Name = "codec_name")] public string? CodecName;
        [DataMember(Name = "profile")] public string? Profile;
        [DataMember(Name = "width")] public int Width;
        [DataMember(Name = "height")] public int Height;
        [DataMember(Name = "pix_fmt")] public string? PixFmt;
        [DataMember(Name = "r_frame_rate")] public string? RFrameRate;
        [DataMember(Name = "avg_frame_rate")] public string? AvgFrameRate;
        [DataMember(Name = "time_base")] public string? TimeBase;
        [DataMember(Name = "bit_rate")] public string? BitRate;
        [DataMember(Name = "sample_rate")] public string? SampleRate;
        [DataMember(Name = "channels")] public int Channels;
        [DataMember(Name = "has_b_frames")] public int HasBFrames;
        [DataMember(Name = "disposition")] public ProbeDisposition? Disposition;
    }
    [DataContract] internal class ProbeDisposition { [DataMember(Name = "attached_pic")] public int AttachedPic; }

    public sealed class AudioInfo
    {
        public int Index;
        public string Codec = "";
        public int SampleRate;
        public int Channels;
        public long BitRate;
    }

    /// <summary>Thong tin video doc bang ffprobe.</summary>
    public sealed class MediaInfo
    {
        public string Path = "";
        public double Duration;
        /// <summary>start_time cua file. Moi thoi diem trong app tinh tu 0 (= pts - StartTime), giong mpv va ffmpeg -ss.</summary>
        public double StartTime;
        public string FormatName = "";
        public long BitRate;

        public bool HasVideo;
        public int VideoIndex = -1;
        public string VideoCodec = "";
        public string VideoProfile = "";
        public int Width, Height;
        public string PixFmt = "";
        public double Fps;          // r_frame_rate (fps danh nghia)
        public double AvgFps;       // avg_frame_rate
        public long VideoBitRate;
        public bool HasBFrames;

        public List<AudioInfo> Audio = new List<AudioInfo>();

        /// <summary>Thoi diem (giay) cua moi khung hinh, da sap xep (theo thu tu hien thi).</summary>
        public double[] FrameTimes = Array.Empty<double>();
        /// <summary>Thoi diem cac keyframe, da sap xep.</summary>
        public double[] Keyframes = Array.Empty<double>();
        /// <summary>DTS (thu tu giai ma) cua tung keyframe, cung chi so voi Keyframes. Khac pts khi video co B-frame.</summary>
        public double[] KeyframeDts = Array.Empty<double>();
        public bool FramesReady;

        public bool IsVfr => Fps > 0 && AvgFps > 0 && Math.Abs(Fps - AvgFps) / Fps > 0.02;
        public double FrameDuration => Fps > 0 ? 1.0 / Fps : 1.0 / 30;
        /// <summary>Thoi diem ket thuc khung hinh cuoi (do dai file co the dai hon do phan tieng du).</summary>
        public double VideoEnd => FrameTimes.Length > 0 ? Math.Min(Duration, FrameTimes[FrameTimes.Length - 1] + FrameDuration) : Duration;
        public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();

        public string Describe()
        {
            var sb = new StringBuilder();
            if (HasVideo) sb.Append($"{VideoCodec.ToUpperInvariant()} {Width}×{Height} • {(IsVfr ? AvgFps : Fps):0.##} fps{(IsVfr ? " (VFR)" : "")}");
            foreach (var a in Audio) sb.Append($" • {a.Codec.ToUpperInvariant()} {a.SampleRate / 1000.0:0.#} kHz {(a.Channels == 1 ? "mono" : a.Channels == 2 ? "stereo" : a.Channels + "ch")}");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- tra cuu khung hinh

        /// <summary>Chi so khung hinh dang hien tai thoi diem t (khung cuoi cung co time &lt;= t).</summary>
        public int FrameIndexAt(double t)
        {
            var f = FrameTimes;
            if (f.Length == 0) return (int)Math.Floor(t * (Fps > 0 ? Fps : 30) + 1e-6);
            int i = Array.BinarySearch(f, t + 1e-6);
            if (i < 0) i = ~i - 1;
            return Math.Max(0, Math.Min(f.Length - 1, i));
        }

        /// <summary>Thoi diem bat dau cua khung hinh dang hien tai t (de diem cat trung ranh gioi khung).</summary>
        public double SnapToFrame(double t)
        {
            if (FrameTimes.Length == 0)
            {
                double fd = FrameDuration;
                return Math.Max(0, Math.Round(t / fd) * fd);
            }
            if (t >= Duration - 1e-6) return Duration;
            return FrameTimes[FrameIndexAt(t)];
        }

        public double FrameTime(int index)
        {
            if (FrameTimes.Length == 0) return index * FrameDuration;
            if (index <= 0) return FrameTimes.Length > 0 ? FrameTimes[0] : 0;
            if (index >= FrameTimes.Length) return Duration;
            return FrameTimes[index];
        }

        /// <summary>Keyframe gan nhat &lt;= t.</summary>
        public double KeyframeAtOrBefore(double t)
        {
            var k = Keyframes;
            if (k.Length == 0) return 0;
            int i = Array.BinarySearch(k, t + 1e-6);
            if (i < 0) i = ~i - 1;
            return i < 0 ? k[0] : k[i];
        }

        /// <summary>Keyframe gan nhat &gt;= t (hoac het video).</summary>
        public double KeyframeAtOrAfter(double t)
        {
            var k = Keyframes;
            if (k.Length == 0) return Duration;
            int i = Array.BinarySearch(k, t - 1e-6);
            if (i < 0) i = ~i;
            return i >= k.Length ? Duration : k[i];
        }

        public double PrevKeyframe(double t)
        {
            var k = Keyframes;
            if (k.Length == 0) return 0;
            int i = Array.BinarySearch(k, t - 1e-4);
            if (i < 0) i = ~i;
            i--;
            return i < 0 ? 0 : k[i];
        }

        public double NextKeyframe(double t)
        {
            var k = Keyframes;
            if (k.Length == 0) return Duration;
            int i = Array.BinarySearch(k, t + 1e-4);
            if (i < 0) i = ~i; else i++;
            return i >= k.Length ? Duration : k[i];
        }

        public bool IsKeyframe(double t, double tol = 0.0015)
        {
            if (Keyframes.Length == 0) return false;
            return Math.Abs(KeyframeAtOrBefore(t + tol) - t) <= tol;
        }

        /// <summary>DTS cua keyframe co pts = t (hoac t neu khong tim thay).</summary>
        public double KeyframeDtsOf(double t)
        {
            int i = Array.BinarySearch(Keyframes, t - 1e-6);
            if (i < 0) i = ~i;
            if (i < Keyframes.Length && Math.Abs(Keyframes[i] - t) < 0.0015 && i < KeyframeDts.Length) return KeyframeDts[i];
            return t;
        }

        // ---------------------------------------------------------------- doc thong tin

        private static double ParseRate(string? r)
        {
            if (string.IsNullOrEmpty(r)) return 0;
            var p = r!.Split('/');
            if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b != 0) return a / b;
            return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private static double D(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        private static long L(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
        {
            string args = "-v error -show_entries format=duration,start_time,format_name,bit_rate:stream=index,codec_type,codec_name,profile,width,height,pix_fmt,r_frame_rate,avg_frame_rate,time_base,bit_rate,sample_rate,channels,has_b_frames:stream_disposition=attached_pic -of json " + Tools.Quote(path);
            var r = await Tools.RunAsync(Tools.FFprobe, args, ct).ConfigureAwait(false);
            if (r.Exit != 0 && string.IsNullOrWhiteSpace(r.Out)) throw new InvalidOperationException("ffprobe: " + r.Err.Trim());
            ProbeRoot root;
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(r.Out)))
                root = (ProbeRoot)new DataContractJsonSerializer(typeof(ProbeRoot)).ReadObject(ms);

            var mi = new MediaInfo { Path = path };
            mi.Duration = D(root.Format?.Duration);
            mi.StartTime = D(root.Format?.StartTime);
            mi.FormatName = root.Format?.FormatName ?? "";
            mi.BitRate = L(root.Format?.BitRate);
            foreach (var s in root.Streams ?? new List<ProbeStream>())
            {
                if (s.CodecType == "video" && !mi.HasVideo && (s.Disposition?.AttachedPic ?? 0) == 0)
                {
                    mi.HasVideo = true;
                    mi.VideoIndex = s.Index;
                    mi.VideoCodec = s.CodecName ?? "";
                    mi.VideoProfile = s.Profile ?? "";
                    mi.Width = s.Width; mi.Height = s.Height;
                    mi.PixFmt = s.PixFmt ?? "";
                    mi.Fps = ParseRate(s.RFrameRate);
                    mi.AvgFps = ParseRate(s.AvgFrameRate);
                    if (mi.Fps <= 0 || mi.Fps > 240) mi.Fps = mi.AvgFps;
                    mi.VideoBitRate = L(s.BitRate);
                    mi.HasBFrames = s.HasBFrames > 0;
                }
                else if (s.CodecType == "audio")
                {
                    mi.Audio.Add(new AudioInfo { Index = s.Index, Codec = s.CodecName ?? "", SampleRate = (int)D(s.SampleRate), Channels = s.Channels, BitRate = L(s.BitRate) });
                }
            }
            return mi;
        }

        /// <summary>
        /// Doc thoi diem moi khung hinh + keyframe tu goi du lieu (khong giai ma, rat nhanh:
        /// ~6 giay cho video 1h30). Chay nen sau khi mo file.
        /// </summary>
        public async Task LoadFramesAsync(CancellationToken ct = default)
        {
            if (!HasVideo) { FramesReady = true; return; }
            var frames = new List<double>(Duration > 0 && Fps > 0 ? (int)Math.Min(5_000_000, Duration * Fps * 1.05) : 4096);
            var keys = new List<(double Pts, double Dts)>(1024);
            string args = $"-v error -select_streams v:0 -show_entries packet=pts_time,dts_time,flags -of csv=p=0 {Tools.Quote(Path)}";
            await Tools.RunAsync(Tools.FFprobe, args, ct, onStdoutLine: line =>
            {
                // "pts_time,dts_time,flags"
                var p = line.Split(',');
                if (p.Length < 3) return;
                if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) return;
                t -= StartTime;
                frames.Add(t);
                if (p[2].Length > 0 && p[2][0] == 'K')
                {
                    double dts = double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) ? dv - StartTime : t;
                    keys.Add((t, dts));
                }
            }).ConfigureAwait(false);
            frames.Sort();
            keys.Sort((a, b) => a.Pts.CompareTo(b.Pts));
            // bo thoi diem trung (goi rong)
            var f = new List<double>(frames.Count);
            foreach (var t in frames) if (f.Count == 0 || t - f[f.Count - 1] > 1e-7) f.Add(t);
            FrameTimes = f.ToArray();
            Keyframes = keys.Select(k => k.Pts).ToArray();
            KeyframeDts = keys.Select(k => k.Dts).ToArray();
            FramesReady = true;
        }
    }
}
