using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brisk.Native;

namespace Brisk.Services;

/// <summary>Exe path -> frozen WPF icon + friendly name, cached for the app lifetime.</summary>
public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> Names = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (Icons)
        {
            if (Icons.TryGetValue(path, out var cached)) return cached;
        }
        ImageSource? img = null;
        try
        {
            var info = new NativeMethods.SHFILEINFO();
            NativeMethods.SHGetFileInfo(path, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
            if (info.hIcon != IntPtr.Zero)
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                img = bmp;
                NativeMethods.DestroyIcon(info.hIcon);
            }
        }
        catch { }
        lock (Icons) Icons[path] = img;
        return img;
    }

    private static readonly Dictionary<string, ImageSource> Avatars = new();
    private static readonly Color[] AvatarColors =
    [
        Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xEC, 0x48, 0x99),
        Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x06, 0xB6, 0xD4),
        Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0x64, 0x74, 0x8B),
    ];

    /// <summary>The exe's icon, or a coloured rounded tile with the app's initial when there isn't one.</summary>
    public static ImageSource GetOrAvatar(string? path, string name) => Get(path) ?? Avatar(name);

    public static ImageSource Avatar(string name)
    {
        string key = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim();
        lock (Avatars)
        {
            if (Avatars.TryGetValue(key, out var cached)) return cached;
        }
        char letter = char.ToUpperInvariant(key.FirstOrDefault(char.IsLetterOrDigit, '?'));
        int hash = 0;
        foreach (char c in key) hash = unchecked(hash * 31 + c);
        var color = AvatarColors[(hash & 0x7FFFFFFF) % AvatarColors.Length];

        var bg = new SolidColorBrush(Color.FromArgb(56, color.R, color.G, color.B));
        var fg = new SolidColorBrush(Color.FromRgb((byte)Math.Min(255, color.R + 70), (byte)Math.Min(255, color.G + 70), (byte)Math.Min(255, color.B + 70)));
        var text = new FormattedText(letter.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 17, fg, 1.0);
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, 32, 32), 8, 8);
            dc.DrawText(text, new Point((32 - text.Width) / 2, (32 - text.Height) / 2));
        }
        var img = new DrawingImage(group);
        img.Freeze();
        lock (Avatars) Avatars[key] = img;
        return img;
    }

    /// <summary>"Google Chrome" instead of "chrome.exe" when the exe has a description.</summary>
    public static string? FriendlyName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (Names)
        {
            if (Names.TryGetValue(path, out var cached)) return cached;
        }
        string? name = null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            name = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription.Trim()
                 : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName.Trim() : null;
        }
        catch { }
        lock (Names) Names[path] = name;
        return name;
    }
}
