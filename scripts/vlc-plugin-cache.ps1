# Writes libVLC's plugin cache (plugins\plugins.dat) next to the bundled plugins. Without it, every new LibVLC
# loads each of the ~320 plugin DLLs to read its description; with it, only the plugins a video needs are loaded.
# libVLC writes the cache itself when started with --reset-plugins-cache, so the cache always matches the bundled
# libvlccore. Entries record each plugin's size and modification time: copies that keep those (build output,
# publish, the installer) stay valid, and libVLC rescans any plugin whose entry no longer matches.
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$library = Join-Path (Resolve-Path $Directory) 'libvlc.dll'
if (-not (Test-Path $library)) { throw "libvlc.dll not found in $Directory" }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class VlcPluginCache
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr New(int argc, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] argv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Release(IntPtr instance);
    public static void Write(string library)
    {
        // LOAD_WITH_ALTERED_SEARCH_PATH: libvlccore.dll resolves from libvlc.dll's own folder.
        var module = LoadLibraryEx(library, IntPtr.Zero, 0x8);
        if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var create = Marshal.GetDelegateForFunctionPointer<New>(GetProcAddress(module, "libvlc_new"));
        var release = Marshal.GetDelegateForFunctionPointer<Release>(GetProcAddress(module, "libvlc_release"));
        var instance = create(2, new[] { "--quiet", "--reset-plugins-cache" });
        if (instance == IntPtr.Zero) throw new InvalidOperationException("libVLC did not start");
        release(instance);
    }
}
'@
$cache = Join-Path (Split-Path $library) 'plugins\plugins.dat'
Remove-Item $cache -ErrorAction SilentlyContinue
[VlcPluginCache]::Write($library)
if (-not (Test-Path $cache)) { throw "libVLC did not write $cache" }
