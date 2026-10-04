using System.Drawing.Drawing2D;

namespace PhoneMicReceiver;

/// <summary>Draws the window/tray icon at runtime so the build needs no binary assets.</summary>
public static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(30, 111, 217));
            g.FillEllipse(bg, 0, 0, 31, 31);
            using var fg = new SolidBrush(Color.White);
            g.FillEllipse(fg, 12, 5, 8, 8);
            g.FillRectangle(fg, 12, 9, 8, 8);
            g.FillEllipse(fg, 12, 13, 8, 8);
            using var pen = new Pen(Color.White, 2f);
            g.DrawArc(pen, 9, 11, 14, 12, 0, 180);
            g.DrawLine(pen, 16, 23, 16, 26);
            g.DrawLine(pen, 12, 26, 20, 26);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
