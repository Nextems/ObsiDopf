using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ObsidianDocs;

internal static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        int enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        // DWM colors use COLORREF (0x00BBGGRR), rather than RGB.
        int caption = 0x00241E1E; // #1E1E24
        int text = 0x00E2D5D8;    // #D8D5E2
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }
}
