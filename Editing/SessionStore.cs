using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace VideoCutterApp.Editing
{
    [DataContract]
    public sealed class SessionSeg
    {
        [DataMember(Name = "s")] public double Start;
        [DataMember(Name = "e")] public double End;
        [DataMember(Name = "keep")] public bool Keep;
        [DataMember(Name = "order")] public int Order;
    }

    /// <summary>Trang thai lam viec cua 1 video (diem cat, giu/bo, thu tu, vi tri dang xem).</summary>
    [DataContract]
    public sealed class Session
    {
        [DataMember(Name = "version")] public int Version = 1;
        [DataMember(Name = "fingerprint")] public string Fingerprint = "";
        [DataMember(Name = "videoPath")] public string VideoPath = "";
        [DataMember(Name = "videoSize")] public long VideoSize;
        [DataMember(Name = "duration")] public double Duration;
        [DataMember(Name = "segments")] public List<SessionSeg>? Segments;
        [DataMember(Name = "playhead")] public double Playhead;
        [DataMember(Name = "output")] public string? Output;
        [DataMember(Name = "savedUtc")] public DateTime SavedUtc;

        public string VideoName => Path.GetFileName(VideoPath);
        public int Count => Segments?.Count ?? 0;
        public int KeptCount => Segments?.Count(s => s.Keep) ?? 0;
        public double KeptDuration => Segments?.Where(s => s.Keep).Sum(s => s.End - s.Start) ?? 0;
    }

    /// <summary>
    /// Luu phien trong %AppData%\VideoCutter\sessions (khong dong vao thu muc video).
    /// Moi phien gan voi dung 1 video qua "dau van tay": kich thuoc + SHA1 cua 1 MB dau va 1 MB cuoi file
    /// -> doi ten / chuyen thu muc van nhan ra; file bi ghi de / nen lai thi khong ap nham.
    /// </summary>
    public static class SessionStore
    {
        /// <summary>Doi thu muc luu (de kiem thu).</summary>
        public static string? DirOverride;

        public static string Dir => DirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoCutter", "sessions");

        private const int Chunk = 1 << 20;

        public static string Fingerprint(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA1.Create())
            {
                long len = fs.Length;
                var buf = new byte[Chunk];
                int n = ReadFull(fs, buf, (int)Math.Min(Chunk, len));
                sha.TransformBlock(buf, 0, n, null, 0);
                if (len > Chunk)
                {
                    fs.Seek(Math.Max(Chunk, len - Chunk), SeekOrigin.Begin);
                    n = ReadFull(fs, buf, Chunk);
                    sha.TransformBlock(buf, 0, n, null, 0);
                }
                var lenBytes = BitConverter.GetBytes(len);
                sha.TransformFinalBlock(lenBytes, 0, lenBytes.Length);
                var sb = new StringBuilder();
                foreach (var b in sha.Hash.Take(12)) sb.Append(b.ToString("x2"));
                return len.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + sb;
            }
        }

        private static int ReadFull(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count) { int r = s.Read(buf, got, count - got); if (r <= 0) break; got += r; }
            return got;
        }

        private static string FileOf(string fp) => Path.Combine(Dir, fp + ".json");
        private static readonly DataContractJsonSerializer Ser = new DataContractJsonSerializer(typeof(Session));

        public static Session? Load(string fp)
        {
            try
            {
                string f = FileOf(fp);
                if (!File.Exists(f)) return null;
                using (var fs = File.OpenRead(f)) return Ser.ReadObject(fs) as Session;
            }
            catch { return null; }
        }

        public static void Save(Session s)
        {
            Directory.CreateDirectory(Dir);
            string f = FileOf(s.Fingerprint), tmp = f + ".tmp";
            using (var fs = File.Create(tmp)) Ser.WriteObject(fs, s);
            if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
        }

        public static void Delete(string fp)
        {
            try { string f = FileOf(fp); if (File.Exists(f)) File.Delete(f); } catch { }
        }

        /// <summary>Xoa moi phien (chi file .json trong thu muc sessions).</summary>
        public static void DeleteAll()
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                foreach (var f in Directory.GetFiles(Dir, "*.json")) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }

        public static List<Session> All()
        {
            var r = new List<Session>();
            try
            {
                if (!Directory.Exists(Dir)) return r;
                foreach (var f in Directory.GetFiles(Dir, "*.json"))
                {
                    try { using (var fs = File.OpenRead(f)) if (Ser.ReadObject(fs) is Session s) r.Add(s); } catch { }
                }
            }
            catch { }
            return r.OrderByDescending(s => s.SavedUtc).ToList();
        }

        /// <summary>Phien cua file cung duong dan nhung dau van tay khac (video da bi thay doi).</summary>
        public static Session? StaleFor(string path, string fp) =>
            All().FirstOrDefault(s => s.Fingerprint != fp && string.Equals(s.VideoPath, path, StringComparison.OrdinalIgnoreCase));
    }
}
