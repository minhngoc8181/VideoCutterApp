using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VideoCutterApp
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

        /// <summary>File mo khi khoi dong (keo tha vao exe / "Open with").</summary>
        public static string? StartupFile { get; private set; }
        /// <summary>--snapshot out.png [file]: chup giao dien roi thoat (de kiem tra bo cuc).</summary>
        public static string? SnapshotPath { get; private set; }

        public static string LogPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VideoCutter.log");

        public static void WriteLog(string s)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {s}{Environment.NewLine}"); } catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            var args = e.Args;
            DispatcherUnhandledException += (s, ex) =>
            {
                WriteLog("Lỗi: " + ex.Exception);
                MessageBox.Show(ex.Exception.Message, "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            if (args.Length > 0 && args[0] == "--selftest")
            {
                AttachConsole(-1);
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string? dir = args.Length > 1 ? args[1] : null;
                string? real = args.Length > 2 ? args[2] : null;
                Task.Run(() => Tests.SelfTest.RunAsync(dir, real)).ContinueWith(t =>
                {
                    int code = t.IsFaulted ? 3 : t.Result;
                    if (t.IsFaulted) Console.WriteLine(t.Exception);
                    Dispatcher.Invoke(() => Shutdown(code));
                });
                return;
            }

            if (args.Length > 1 && args[0] == "--snapshot")
            {
                SnapshotPath = args[1];
                if (args.Length > 2) StartupFile = args[2];
            }
            else if (args.Length > 0 && File.Exists(args[0])) StartupFile = args[0];

            base.OnStartup(e);
            new MainWindow().Show();
        }
    }
}
