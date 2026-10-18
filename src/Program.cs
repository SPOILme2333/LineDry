// Entry point. One instance only: the line is a place, not a window.
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace LineDry
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex guard = new Mutex(true, "LineDry.SingleInstance.v1", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                try
                {
                    using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                        Ui.DpiScale = g.DpiX / 96.0;
                }
                catch { Ui.DpiScale = 1.0; }

                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
                {
                    Log("unhandled: " + e.ExceptionObject);
                };
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
                {
                    Log("thread: " + e.Exception);
                };

                Application.Run(new Launcher());
            }
        }

        private static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LineDry");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("s") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
