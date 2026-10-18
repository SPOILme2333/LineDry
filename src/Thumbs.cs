// Thumbnails. Images go through GDI+; screen recordings fall back to the
// shell's own thumbnail provider, which is the Windows stand-in for grabbing
// a video's first frame.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LineDry
{
    internal static class Thumbs
    {
        /// The original caps a card's thumbnail at 320 points on its long edge.
        public static int DefaultMaxPixels
        {
            get
            {
                // The original caps at 320 points, but a card never shows more
                // than CardWidth x PhotoMaxHeight of it. Keeping the old cap cost
                // five times the pixels for every picture on the line; this keeps
                // a fifth of the memory and is still larger than anything drawn.
                return (int)Math.Round(150 * Geo.Scale * Ui.DpiScale);
            }
        }

        private static readonly string[] ImageExt =
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic" };

        private static readonly string[] VideoExt = { ".mov", ".mp4", ".m4v", ".avi", ".wmv", ".mkv" };

        public static bool IsRecording(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(VideoExt, e) >= 0;
        }

        public static bool IsImage(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(ImageExt, e) >= 0;
        }

        public static Bitmap Make(string path, int maxPixels)
        {
            try
            {
                if (IsRecording(path)) return FromShell(path, maxPixels);
                // Scale straight out of the decoder: LoadPlain makes a full-size
                // copy first, and on a 2560x1600 screenshot that copy alone is
                // most of the work. The file is still never locked.
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image img = Image.FromStream(fs, false, false))
                {
                    return Scale(img, maxPixels);
                }
            }
            catch { return null; }
        }

        /// Decodes without holding a lock on the file, so the line can sit on
        /// a screenshot while the screenshot app still owns it.
        public static Bitmap LoadPlain(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    using (Image img = Image.FromStream(fs, false, false))
                    {
                        return new Bitmap(img);
                    }
                }
            }
            catch { return null; }
        }

        public static Bitmap Scale(Image src, int maxPixels)
        {
            double sw = src.Width, sh = src.Height;
            if (sw <= 0 || sh <= 0) return null;
            double k = Math.Min(1.0, maxPixels / Math.Max(sw, sh));
            int w = Math.Max(1, (int)Math.Round(sw * k));
            int h = Math.Max(1, (int)Math.Round(sh * k));

            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        // ------------------------------------------------------------ shell thumbs

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(Native.SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        private const int SIIGBF_RESIZETOFIT = 0x00;
        private const int SIIGBF_BIGGERSIZEOK = 0x01;

        /// Asks Windows for a thumbnail of anything it knows how to thumb --
        /// here, a screen recording, which shows its first frame.
        private static Bitmap FromShell(string path, int maxPixels)
        {
            IntPtr hbm = IntPtr.Zero;
            try
            {
                IShellItemImageFactory factory;
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
                if (factory == null) return null;
                factory.GetImage(new Native.SIZE(maxPixels, maxPixels), SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK, out hbm);
                if (hbm == IntPtr.Zero) return null;
                using (Bitmap raw = Image.FromHbitmap(hbm))
                {
                    Bitmap copy = new Bitmap(raw);
                    return copy;
                }
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
            }
        }
    }
}
