// Geometry and metrics, ported one-for-one from the original Layout enum so
// the line looks and behaves the same on Windows as it does on macOS.
using System;

namespace LineDry
{
    internal enum LineSize { Small, Medium, Large }

    internal static class Geo
    {
        public static double Scale
        {
            get
            {
                string s = Settings.GetString("lineSize", "medium");
                if (s == "small") return 0.8;
                if (s == "large") return 1.35;
                return 1.0;
            }
        }

        public static void SetSize(LineSize size)
        {
            string s = size == LineSize.Small ? "small" : (size == LineSize.Large ? "large" : "medium");
            Settings.SetString("lineSize", s);
        }

        public static LineSize CurrentSize
        {
            get
            {
                double s = Scale;
                if (s < 0.9) return LineSize.Small;
                if (s > 1.2) return LineSize.Large;
                return LineSize.Medium;
            }
        }

        public static double PhotoMaxHeight { get { return Math.Round(104 * Scale * Ui.DpiScale); } }
        public static double CardWidth { get { return Math.Round(150 * Scale * Ui.DpiScale); } }
        public static double Spacing { get { return CardWidth + 24 * Ui.DpiScale; } }
        public static double PanelHeight { get { return PhotoMaxHeight + 106 * Ui.DpiScale; } }
        public static double RopeTop { get { return 10 * Ui.DpiScale; } }
        public static double PinAbove { get { return 9.5 * Ui.DpiScale; } }
        public static double FrameRadius { get { return 16 * Ui.DpiScale; } }
        public static double FrameInset { get { return 4 * Ui.DpiScale; } }
        /// Distance from the clip's top edge to the card's top edge: 26 - 12.
        public static double CardOffsetBelowTop { get { return (26 - 12) * Ui.DpiScale; } }
        public static double ClipWidth { get { return 9 * Ui.DpiScale; } }
        public static double ClipHeight { get { return 26 * Ui.DpiScale; } }

        /// The rope hangs as a parabola from edge to edge of the screen.
        public static double Sag(double width) { return Math.Min(30 * Ui.DpiScale, width * 0.018); }

        public static double RopeY(double x, double width)
        {
            if (width <= 0) return RopeTop;
            double f = x / width;
            return RopeTop + 4 * Sag(width) * f * (1 - f);
        }

        /// Where the card at `index` hangs. Ported verbatim, including the two
        /// regimes: a pinned-length line keeps its spacing and scrolls, a
        /// fitted line pulls the photos closer together instead.
        public static double X(int index, int count, double width, int visible, double scroll, bool pinned)
        {
            if (pinned && count > visible)
            {
                double newest = width / 2 + (visible - 1) * Spacing / 2;
                return newest - (count - 1 - index) * Spacing + scroll;
            }
            double gaps = Math.Max(count - 1, 0);
            double step = gaps > 0 ? Math.Min(Spacing, Math.Max(0, width - 200 * Ui.DpiScale) / gaps) : Spacing;
            return width / 2 - gaps * step / 2 + index * step;
        }

        /// How many photos fit across the screen.
        public static int VisibleCount(double width)
        {
            double usable = width - 200 * Ui.DpiScale;
            int fits = (int)(usable / Spacing);
            if (fits < 3) fits = 3;
            if (fits > 12) fits = 12;
            return fits;
        }
    }
}
