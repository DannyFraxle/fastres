using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;

namespace FastRes;

/// <summary>Draws the tray icon from the system's Fluent icon font so it matches the other tray icons.</summary>
internal static class IconRenderer
{
    public const string MonitorGlyph = "";

    private static string? _glyphFamily;

    public static string GlyphFamily => _glyphFamily ??= PickGlyphFamily();

    private static string PickGlyphFamily()
    {
        using var installed = new InstalledFontCollection();
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            if (installed.Families.Any(f => f.Name == name)) return name;
        return "Segoe UI Symbol";
    }

    /// <summary>Returns an HICON the caller must destroy with <see cref="Native.DestroyIcon"/>.</summary>
    public static IntPtr CreateTrayIcon(int size, bool darkTaskbar)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font(GlyphFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(darkTaskbar ? Color.White : Color.FromArgb(255, 26, 26, 26));
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.NoWrap,
            };
            g.DrawString(MonitorGlyph, font, brush, new RectangleF(0, 0, size, size), format);
        }
        return bmp.GetHicon();
    }
}
