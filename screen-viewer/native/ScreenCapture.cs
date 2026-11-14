using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int istep, IntPtr hbrFlickerFree, int diFlags);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

    const int CURSOR_SHOWING = 1;
    const int DI_NORMAL = 3;

    static int Main(string[] args)
    {
        try
        {
            SetProcessDPIAware();
        }
        catch { }

        string outPath = null;
        string display = "primary";
        int maxWidth = 1920;
        bool includeCursor = true;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
            else if (args[i] == "--display" && i + 1 < args.Length) display = args[++i];
            else if (args[i] == "--max-width" && i + 1 < args.Length) int.TryParse(args[++i], out maxWidth);
            else if (args[i] == "--no-cursor") includeCursor = false;
        }
        if (maxWidth < 200) maxWidth = 200;
        if (maxWidth > 7680) maxWidth = 7680;

        Rectangle bounds;
        try
        {
            bounds = ResolveBounds(display);
        }
        catch (Exception ex)
        {
            Fail("display: " + ex.Message);
            return 2;
        }
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            Fail("empty display bounds");
            return 2;
        }

        int width = bounds.Width;
        int height = bounds.Height;
        if (width > maxWidth)
        {
            height = (int)Math.Round(height * (double)maxWidth / width);
            width = maxWidth;
        }
        if (height < 1) height = 1;

        string temp = outPath;
        if (string.IsNullOrEmpty(temp))
        {
            temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-screen-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".png");
        }

        POINT cursorPoint;
        bool haveCursor = GetCursorPos(out cursorPoint);

        try
        {
            using (Bitmap full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(full))
                {
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
                }

                Bitmap final = full;
                if (width != bounds.Width || height != bounds.Height)
                {
                    final = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    using (Graphics g2 = Graphics.FromImage(final))
                    {
                        g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g2.DrawImage(full, new Rectangle(0, 0, width, height));
                    }
                }

                if (includeCursor && haveCursor)
                {
                    try { DrawCursor(final, bounds, width, height, cursorPoint); }
                    catch { }
                }

                final.Save(temp, ImageFormat.Png);
                if (final != full) final.Dispose();
            }

            int cursorX = haveCursor ? cursorPoint.X : -1;
            int cursorY = haveCursor ? cursorPoint.Y : -1;
            CURSORINFO ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            bool cursorVisible = GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) != 0;
            Console.Out.Write("{\"ok\":true,\"path\":\"" + Escape(temp) + "\",\"width\":" + width + ",\"height\":" + height
                + ",\"screenX\":" + bounds.X + ",\"screenY\":" + bounds.Y
                + ",\"display\":\"" + Escape(display) + "\""
                + ",\"cursorX\":" + cursorX + ",\"cursorY\":" + cursorY
                + ",\"cursorVisible\":" + (cursorVisible ? "true" : "false") + "}");
            Console.Out.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            Fail(ex.GetType().Name + ": " + ex.Message);
            return 3;
        }
    }

    static Rectangle ResolveBounds(string display)
    {
        Screen[] screens = Screen.AllScreens;
        if (display == null) display = "primary";
        if (display == "all")
        {
            Rectangle r = Screen.PrimaryScreen.Bounds;
            for (int i = 0; i < screens.Length; i++) r = Rectangle.Union(r, screens[i].Bounds);
            return r;
        }
        if (display == "cursor")
        {
            Point p = Cursor.Position;
            for (int i = 0; i < screens.Length; i++)
            {
                if (screens[i].Bounds.Contains(p)) return screens[i].Bounds;
            }
            return Screen.PrimaryScreen.Bounds;
        }
        int index;
        if (int.TryParse(display, out index))
        {
            if (index < 0 || index >= screens.Length) throw new ArgumentException("monitor index " + index + " out of range (0.." + (screens.Length - 1) + ")");
            return screens[index].Bounds;
        }
        return Screen.PrimaryScreen.Bounds;
    }

    static void DrawCursor(Bitmap target, Rectangle bounds, int width, int height, POINT cursorPoint)
    {
        int localX = cursorPoint.X - bounds.X;
        int localY = cursorPoint.Y - bounds.Y;
        if (localX < -64 || localY < -64 || localX > bounds.Width + 64 || localY > bounds.Height + 64) return;

        double sx = (double)width / bounds.Width;
        double sy = (double)height / bounds.Height;

        CURSORINFO ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
        bool haveIcon = GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero;
        if (haveIcon)
        {
            ICONINFO info;
            if (!GetIconInfo(ci.hCursor, out info)) haveIcon = false;
            else
            {
                int x = (int)Math.Round(localX * sx) - (int)Math.Round(info.xHotspot * sx);
                int y = (int)Math.Round(localY * sy) - (int)Math.Round(info.yHotspot * sy);
                using (Graphics g = Graphics.FromImage(target))
                {
                    IntPtr hdc = g.GetHdc();
                    DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
                    g.ReleaseHdc(hdc);
                }
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            }
        }

        if (!haveIcon)
        {
            int x = (int)Math.Round(localX * sx);
            int y = (int)Math.Round(localY * sy);
            using (Graphics g = Graphics.FromImage(target))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Point[] pts = new Point[] {
                    new Point(x, y), new Point(x, y + 18), new Point(x + 5, y + 13),
                    new Point(x + 9, y + 21), new Point(x + 12, y + 19), new Point(x + 8, y + 12),
                    new Point(x + 15, y + 11)
                };
                using (SolidBrush white = new SolidBrush(Color.White))
                using (Pen outline = new Pen(Color.Black, 1.4f))
                {
                    g.FillPolygon(white, pts);
                    g.DrawPolygon(outline, pts);
                }
            }
        }
    }

    static string Escape(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    static void Fail(string message)
    {
        Console.Out.Write("{\"ok\":false,\"error\":\"" + Escape(message) + "\"}");
        Console.Out.Flush();
    }
}
