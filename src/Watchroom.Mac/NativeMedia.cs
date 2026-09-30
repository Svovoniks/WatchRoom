using System.Reflection;
using System.Runtime.InteropServices;
using DataChannelDotnet.Impl;

namespace Watchroom.Mac;

internal static class NativeMedia
{
    private static bool initialized;
    private static nint dataChannelHandle;
    public static void Initialize()
    {
        if (initialized) return;
        if (OperatingSystem.IsMacOS())
        {
            // DataChannelDotnet bundles Intel only. Prefer a matching Homebrew
            // build on Apple Silicon and allow an explicit native library path.
            NativeLibrary.SetDllImportResolver(typeof(RtcPeerConnection).Assembly, Resolve);
            var libDirectory = Environment.GetEnvironmentVariable("WATCHROOM_VLC_PATH") ?? "/Applications/VLC.app/Contents/MacOS/lib";
            if (!File.Exists(Path.Combine(libDirectory, "libvlc.dylib")))
                throw new FileNotFoundException("Install VLC in /Applications, or set WATCHROOM_VLC_PATH to its lib directory, then restart Watchroom.");
            var plugins = Path.GetFullPath(Path.Combine(libDirectory, "..", "plugins"));
            if (Directory.Exists(plugins)) Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", plugins);
            LibVLCSharp.Shared.Core.Initialize(libDirectory);
        }
        else LibVLCSharp.Shared.Core.Initialize(Environment.GetEnvironmentVariable("WATCHROOM_VLC_PATH"));
        initialized = true;
    }
    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? path)
    {
        if (!libraryName.Contains("datachannel", StringComparison.OrdinalIgnoreCase)) return 0;
        if (dataChannelHandle != 0) return dataChannelHandle;
        var custom = Environment.GetEnvironmentVariable("WATCHROOM_DATACHANNEL_NATIVE");
        var candidates = new[] { custom, Path.Combine(AppContext.BaseDirectory, "libdatachannel.dylib"), "/opt/homebrew/lib/libdatachannel.dylib", "/usr/local/lib/libdatachannel.dylib", Path.Combine(AppContext.BaseDirectory, "datachannel.dylib") };
        foreach (var candidate in candidates)
            if (candidate is not null && File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out dataChannelHandle)) return dataChannelHandle;
        throw new DllNotFoundException("WebRTC requires libdatachannel for this Mac. Follow MAC-CLIENT.md to build it, then set WATCHROOM_DATACHANNEL_NATIVE to the matching libdatachannel.dylib path.");
    }
}
