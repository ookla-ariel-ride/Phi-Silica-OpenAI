using System.Runtime.InteropServices;

namespace NpuBridge;

internal static partial class ConsoleWindow
{
    private const int SwHide = 0;

    /// <summary>Hides this process's console window, if it has one. No-op when running headless.</summary>
    public static bool Hide()
    {
        var handle = GetConsoleWindow();
        return handle != IntPtr.Zero && ShowWindow(handle, SwHide);
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
