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
