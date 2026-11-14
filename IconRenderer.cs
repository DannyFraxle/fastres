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
        using var bmp = RenderGlyph(size, darkTaskbar ? Color.White : Color.FromArgb(255, 26, 26, 26));
        return bmp.GetHicon();
    }

    /// <summary>
    /// Draws the glyph at full em size on an oversized canvas (so nothing is clipped by GDI+'s line metrics),
    /// finds the pixels it actually covers and copies them, pixel-aligned and centred, into the icon.
    /// </summary>
    internal static Bitmap RenderGlyph(int size, Color color)
    {
        int pad = size;
        int canvas = size + 2 * pad;
        using var scratch = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scratch))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font(GlyphFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.NoWrap,
            };
            g.DrawString(MonitorGlyph, font, brush, new RectangleF(pad, pad, size, size), format);
        }

        var bounds = OpaqueBounds(scratch);
        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        if (bounds.IsEmpty) return result;

        // Glyphs are designed to fit the em box; if one still overflows, scale it down to fit.
        int w = bounds.Width, h = bounds.Height;
        using (var g = Graphics.FromImage(result))
        {
            g.Clear(Color.Transparent);
            if (w <= size && h <= size)
            {
                int x = (size - w) / 2, y = (size - h) / 2;
                g.DrawImage(scratch, new Rectangle(x, y, w, h), bounds, GraphicsUnit.Pixel);
            }
            else
            {
                double scale = Math.Min((double)size / w, (double)size / h);
                int sw = (int)Math.Round(w * scale), sh = (int)Math.Round(h * scale);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(scratch, new Rectangle((size - sw) / 2, (size - sh) / 2, sw, sh), bounds, GraphicsUnit.Pixel);
            }
        }
        return result;
    }

    private static Rectangle OpaqueBounds(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            var row = new byte[data.Stride];
            for (int y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (row[x * 4 + 3] <= 8) continue; // ignore near-invisible antialiasing fringe
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
