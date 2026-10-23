using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VideoCutterApp.Player
{
    /// <summary>P/Invoke toi thieu cho libmpv-2.dll (client API).</summary>
    internal static class MpvNative
    {
        private const string Dll = "libmpv-2.dll";

        public const int FormatNone = 0, FormatString = 1, FormatFlag = 3, FormatInt64 = 4, FormatDouble = 5;

        public const int EventShutdown = 1, EventLogMessage = 2, EventCommandReply = 5, EventStartFile = 6,
            EventEndFile = 7, EventFileLoaded = 8, EventVideoReconfig = 17, EventSeek = 20,
            EventPlaybackRestart = 21, EventPropertyChange = 22;

        [StructLayout(LayoutKind.Sequential)]
        public struct MpvEvent
        {
            public int EventId;
            public int Error;
            public ulong ReplyUserdata;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MpvEventProperty
        {
            public IntPtr Name;
            public int Format;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MpvEventLogMessage
        {
            public IntPtr Prefix;
            public IntPtr Level;
            public IntPtr Text;
            public int LogLevel;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_create();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_initialize(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_wakeup(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_get_property(IntPtr ctx, byte[] name, int format, ref double data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property(IntPtr ctx, byte[] name, int format, ref double data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command(IntPtr ctx, IntPtr[] args);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command_async(IntPtr ctx, ulong replyUserdata, IntPtr[] args);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata, byte[] name, int format);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_request_log_messages(IntPtr ctx, byte[] minLevel);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_free(IntPtr data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_error_string(int error);

        public static byte[] U8(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            Array.Resize(ref b, b.Length + 1);
            return b;
        }

        public static string? FromU8(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            var buf = new byte[len];
            Marshal.Copy(p, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }
    }

    /// <summary>
    /// Bo phat video dung libmpv, ve truc tiep vao mot cua so con (HWND).
    /// Moi su kien mpv duoc xu ly tren luong rieng; cac event C# duoc goi tren luong do
    /// (giao dien phai Dispatcher.BeginInvoke).
    /// </summary>
    public sealed class MpvPlayer : IDisposable
    {
        private IntPtr _ctx;
        private Thread? _eventThread;
        private volatile bool _disposed;

        // Ma theo doi thuoc tinh
        private const ulong IdTimePos = 1, IdDuration = 2, IdPause = 3, IdSpeed = 4, IdEof = 5, IdFps = 6, IdSeeking = 7;

        public double TimePos { get; private set; }
        public double Duration { get; private set; }
        public bool Paused { get; private set; } = true;
        public double Speed { get; private set; } = 1;
        public bool EofReached { get; private set; }
        public bool Seeking { get; private set; }
        public double ContainerFps { get; private set; }

        public event Action? TimeChanged;
        public event Action? StateChanged;
        public event Action? FileLoaded;
        public event Action<string>? EndFile;
        public event Action? PlaybackRestart;
        public event Action<string>? Log;

        public static bool IsAvailable(out string error)
        {
            try { var p = MpvNative.mpv_create(); if (p != IntPtr.Zero) MpvNative.mpv_terminate_destroy(p); error = ""; return p != IntPtr.Zero; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public MpvPlayer(IntPtr hwnd)
        {
            _ctx = MpvNative.mpv_create();
            if (_ctx == IntPtr.Zero) throw new InvalidOperationException("Không khởi tạo được libmpv.");

            Opt("wid", hwnd.ToInt64().ToString(CultureInfo.InvariantCulture));
            Opt("vo", "gpu");
            Opt("hwdec", "auto-safe");
            Opt("keep-open", "always");        // het video thi dung o khung cuoi, khong dong file
            Opt("idle", "yes");
            Opt("pause", "yes");
            Opt("osc", "no");
            Opt("osd-level", "0");
            Opt("input-default-bindings", "no");
            Opt("input-vo-keyboard", "no");    // phim tat do WPF xu ly
            Opt("input-cursor", "no");
            Opt("input-cursor-passthrough", "yes"); // chuot xuyen qua cua so video
            Opt("cursor-autohide", "no");
            Opt("hr-seek", "yes");             // tua chinh xac tung khung
            Opt("hr-seek-framedrop", "no");
            Opt("demuxer-max-back-bytes", "300MiB"); // lui tung khung nhanh hon
            Opt("demuxer-max-bytes", "300MiB");
            Opt("cache", "yes");
            Opt("background-color", "#12151D");
            Opt("terminal", "no");
            Opt("config", "no");
            Opt("ytdl", "no");
            Opt("audio-display", "no");
            Opt("volume-max", "150");

            int err = MpvNative.mpv_initialize(_ctx);
            if (err < 0) throw new InvalidOperationException("mpv_initialize: " + MpvNative.FromU8(MpvNative.mpv_error_string(err)));

            MpvNative.mpv_request_log_messages(_ctx, MpvNative.U8("warn"));
            Observe(IdTimePos, "time-pos", MpvNative.FormatDouble);
            Observe(IdDuration, "duration", MpvNative.FormatDouble);
            Observe(IdPause, "pause", MpvNative.FormatFlag);
            Observe(IdSpeed, "speed", MpvNative.FormatDouble);
            Observe(IdEof, "eof-reached", MpvNative.FormatFlag);
            Observe(IdFps, "container-fps", MpvNative.FormatDouble);
            Observe(IdSeeking, "seeking", MpvNative.FormatFlag);

            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
            _eventThread.Start();
        }

        private void Opt(string name, string value) => MpvNative.mpv_set_option_string(_ctx, MpvNative.U8(name), MpvNative.U8(value));
        private void Observe(ulong id, string name, int format) => MpvNative.mpv_observe_property(_ctx, id, MpvNative.U8(name), format);

        // ---------------------------------------------------------------- lenh

        public void Command(params string[] args)
        {
            if (_ctx == IntPtr.Zero) return;
            var ptrs = new IntPtr[args.Length + 1];
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    var b = MpvNative.U8(args[i]);
                    ptrs[i] = Marshal.AllocHGlobal(b.Length);
                    Marshal.Copy(b, 0, ptrs[i], b.Length);
                }
                MpvNative.mpv_command_async(_ctx, 0, ptrs);
            }
            finally
            {
                for (int i = 0; i < args.Length; i++) if (ptrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(ptrs[i]);
            }
        }

        public void Load(string path)
        {
            EofReached = false;
            TimePos = 0;
            Duration = 0;
            Command("loadfile", path, "replace");
        }

        public void Stop() => Command("stop");

        private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>Tua chinh xac toi thoi diem (giay) - hien dung khung hinh tai do.</summary>
        public void SeekExact(double t) { TimePos = Math.Max(0, t); Command("seek", F(Math.Max(0, t)), "absolute+exact"); }

        /// <summary>Tua nhanh (toi keyframe) - dung khi keo timeline.</summary>
        public void SeekFast(double t) { TimePos = Math.Max(0, t); Command("seek", F(Math.Max(0, t)), "absolute+keyframes"); }

        public void FrameStep() => Command("frame-step");
        public void FrameBackStep() => Command("frame-back-step");

        public void SetPause(bool pause) => SetProp("pause", pause ? "yes" : "no");
        public void TogglePause() => SetPause(!Paused);
        public void SetSpeed(double speed) => SetProp("speed", F(speed));
        public void SetVolume(double vol) => SetProp("volume", F(vol));
        public void SetMute(bool mute) => SetProp("mute", mute ? "yes" : "no");

        public void SetProp(string name, string value)
        {
            if (_ctx == IntPtr.Zero) return;
            MpvNative.mpv_set_property_string(_ctx, MpvNative.U8(name), MpvNative.U8(value));
        }

        public string? GetProp(string name)
        {
            if (_ctx == IntPtr.Zero) return null;
            var p = MpvNative.mpv_get_property_string(_ctx, MpvNative.U8(name));
            if (p == IntPtr.Zero) return null;
            try { return MpvNative.FromU8(p); } finally { MpvNative.mpv_free(p); }
        }

        public double GetDouble(string name)
        {
            double v = 0;
            if (_ctx != IntPtr.Zero) MpvNative.mpv_get_property(_ctx, MpvNative.U8(name), MpvNative.FormatDouble, ref v);
            return v;
        }

        // ---------------------------------------------------------------- su kien

        private void EventLoop()
        {
            var ctx = _ctx;
            while (!_disposed)
            {
                IntPtr evPtr;
                try { evPtr = MpvNative.mpv_wait_event(ctx, 0.5); }
                catch { return; }
                if (evPtr == IntPtr.Zero) continue;
                var ev = Marshal.PtrToStructure<MpvNative.MpvEvent>(evPtr);
                try
                {
                    switch (ev.EventId)
                    {
                        case MpvNative.EventShutdown: return;
                        case MpvNative.EventPropertyChange: OnProperty(ev); break;
                        case MpvNative.EventFileLoaded: EofReached = false; FileLoaded?.Invoke(); break;
                        case MpvNative.EventEndFile: EndFile?.Invoke(ev.Error < 0 ? (MpvNative.FromU8(MpvNative.mpv_error_string(ev.Error)) ?? "error") : "ok"); break;
                        case MpvNative.EventPlaybackRestart: PlaybackRestart?.Invoke(); break;
                        case MpvNative.EventLogMessage:
                            if (ev.Data != IntPtr.Zero)
                            {
                                var lm = Marshal.PtrToStructure<MpvNative.MpvEventLogMessage>(ev.Data);
                                Log?.Invoke($"[mpv:{MpvNative.FromU8(lm.Prefix)}] {MpvNative.FromU8(lm.Text)?.TrimEnd()}");
                            }
                            break;
                    }
                }
                catch (Exception ex) { Log?.Invoke("mpv event error: " + ex.Message); }
            }
        }

        private void OnProperty(MpvNative.MpvEvent ev)
        {
            if (ev.Data == IntPtr.Zero) return;
            var prop = Marshal.PtrToStructure<MpvNative.MpvEventProperty>(ev.Data);
            bool has = prop.Format != MpvNative.FormatNone && prop.Data != IntPtr.Zero;
            double d = 0; bool flag = false;
            if (has && prop.Format == MpvNative.FormatDouble) d = Marshal.PtrToStructure<double>(prop.Data);
            if (has && prop.Format == MpvNative.FormatFlag) flag = Marshal.ReadInt32(prop.Data) != 0;

            switch (ev.ReplyUserdata)
            {
                case IdTimePos: if (has) { TimePos = d; TimeChanged?.Invoke(); } break;
                case IdDuration: Duration = has ? d : 0; StateChanged?.Invoke(); break;
                case IdPause: Paused = !has || flag; StateChanged?.Invoke(); break;
                case IdSpeed: if (has) { Speed = d; StateChanged?.Invoke(); } break;
                case IdEof: EofReached = has && flag; StateChanged?.Invoke(); break;
                case IdFps: ContainerFps = has ? d : 0; break;
                case IdSeeking: Seeking = has && flag; break;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var ctx = _ctx;
            _ctx = IntPtr.Zero;
            if (ctx != IntPtr.Zero)
            {
                try { MpvNative.mpv_wakeup(ctx); } catch { }
                try { _eventThread?.Join(1000); } catch { }
                try { MpvNative.mpv_terminate_destroy(ctx); } catch { }
            }
        }
    }
}
