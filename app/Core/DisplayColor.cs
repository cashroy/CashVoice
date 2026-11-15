using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CashVoice.Core;

/// <summary>
/// Converts an sRGB colour through the display's ICC profile, the same way browsers do.
/// WPF sends raw sRGB values to the screen, so on wide-gamut displays (e.g. Surface "Vivid")
/// its reds look far more saturated than the same hex code on cashroy.github.io.
/// </summary>
public static class DisplayColor
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool GetICMProfileW(IntPtr dc, ref uint size, StringBuilder? name);

    [StructLayout(LayoutKind.Sequential)] struct PROFILE { public uint dwType; public IntPtr pProfileData; public uint cbDataSize; }
    [DllImport("mscms.dll")] static extern IntPtr OpenColorProfileW(ref PROFILE p, uint access, uint share, uint creation);
    [DllImport("mscms.dll")] static extern bool CloseColorProfile(IntPtr p);
    [DllImport("mscms.dll")] static extern IntPtr CreateMultiProfileTransform(IntPtr[] profiles, uint count, uint[] intents, uint intentCount, uint flags, uint cmm);
    [DllImport("mscms.dll")] static extern bool DeleteColorTransform(IntPtr t);
    [StructLayout(LayoutKind.Sequential, Size = 16)] struct COLOR { public ushort r, g, b; }
    [DllImport("mscms.dll")] static extern bool TranslateColors(IntPtr t, COLOR[] input, uint count, int typeIn, [Out] COLOR[] output, int typeOut);

    const uint PROFILE_FILENAME = 1, PROFILE_READ = 1, FILE_SHARE_READ = 1, OPEN_EXISTING = 3;
    const uint INTENT_RELATIVE_COLORIMETRIC = 1, BEST_MODE = 3;
    const int COLOR_RGB = 2;

    static IntPtr Open(string path)
    {
        var p = new PROFILE { dwType = PROFILE_FILENAME, pProfileData = Marshal.StringToHGlobalUni(path),
                              cbDataSize = (uint)((path.Length + 1) * 2) };
        try { return OpenColorProfileW(ref p, PROFILE_READ, FILE_SHARE_READ, OPEN_EXISTING); }
        finally { Marshal.FreeHGlobal(p.pProfileData); }
    }

    /// <summary>The value to send to the primary display so it shows sRGB (r,g,b). Unchanged if there's no profile.</summary>
    public static (byte R, byte G, byte B) ForDisplay(byte r, byte g, byte b)
    {
        try
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            uint size = 260;
            var name = new StringBuilder(260);
            bool ok = GetICMProfileW(dc, ref size, name);
            ReleaseDC(IntPtr.Zero, dc);
            if (!ok) return (r, g, b);
            string display = name.ToString();
            if (display.EndsWith("sRGB Color Space Profile.icm", StringComparison.OrdinalIgnoreCase)) return (r, g, b);

            string srgb = Environment.ExpandEnvironmentVariables(
                @"%WINDIR%\System32\spool\drivers\color\sRGB Color Space Profile.icm");
            IntPtr src = Open(srgb), dst = Open(display);
            IntPtr t = IntPtr.Zero;
            try
            {
                if (src == IntPtr.Zero || dst == IntPtr.Zero) return (r, g, b);
                t = CreateMultiProfileTransform(new[] { src, dst }, 2,
                        new[] { INTENT_RELATIVE_COLORIMETRIC, INTENT_RELATIVE_COLORIMETRIC }, 2, BEST_MODE, 0);
                if (t == IntPtr.Zero) return (r, g, b);
                var input = new[] { new COLOR { r = (ushort)(r * 257), g = (ushort)(g * 257), b = (ushort)(b * 257) } };
                var output = new COLOR[1];
                if (!TranslateColors(t, input, 1, COLOR_RGB, output, COLOR_RGB)) return (r, g, b);
                return ((byte)(output[0].r / 257), (byte)(output[0].g / 257), (byte)(output[0].b / 257));
            }
            finally
            {
                if (t != IntPtr.Zero) DeleteColorTransform(t);
                if (src != IntPtr.Zero) CloseColorProfile(src);
                if (dst != IntPtr.Zero) CloseColorProfile(dst);
            }
        }
        catch { return (r, g, b); }
    }
}
