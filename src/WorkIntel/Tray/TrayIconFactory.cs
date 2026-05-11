using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WorkIntel.App;

namespace WorkIntel.Tray;

/// <summary>
/// Generates tray icons procedurally so the scaffold ships without binary assets.
/// One icon per <see cref="AppState"/>; all are loaded once at startup and live
/// for the app lifetime. Replace with hand-designed .ico files later.
/// </summary>
public static class TrayIconFactory
{
    public static IReadOnlyDictionary<AppState, Icon> CreateAll() =>
        new Dictionary<AppState, Icon>
        {
            [AppState.Active] = Create(Color.FromArgb(46, 204, 113)),  // green
            [AppState.Idle]   = Create(Color.FromArgb(149, 165, 166)), // muted grey
            [AppState.Paused] = Create(Color.FromArgb(241, 196, 15)),  // amber
        };

    public static Icon Create(Color fill, char letter = 'W')
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 1, 1, size - 2, size - 2);

            using var pen = new Pen(Color.FromArgb(160, 0, 0, 0), 1.5f);
            g.DrawEllipse(pen, 1, 1, size - 2, size - 2);

            using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            using var textBrush = new SolidBrush(Color.White);
            g.DrawString(letter.ToString(), font, textBrush, new RectangleF(0, 0, size, size), sf);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            // Clone so the returned Icon owns its own handle data; then we can safely DestroyIcon.
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
