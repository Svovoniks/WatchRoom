using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace Watchroom.Desktop;

// LibVLC draws subtitles into the picture, so the dock covers those near the bottom of it. The video output's
// "sub-margin" variable, which VLC's own subtitle position keys change, raises them by a number of video pixels.
// LibVLCSharp has no API for it, so the output is reached through libvlccore's exports, the way libvlc reaches it.
internal static class VlcSubtitleMargin
{
    // INPUT_GET_VOUTS as LibVLC 3.0 numbers it: libvlc_media_player_has_vout passes 43 to input_Control.
    // The 3.0 series keeps its module ABI, and so this number, fixed.
    private const int InputGetVouts = 43;
    private const int VarInteger = 0x0030; // VLC_VAR_INTEGER

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetInputThread(IntPtr player);
    // input_Control is variadic; on x64 Windows variadic pointer arguments are passed like fixed ones.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InputControl(IntPtr input, int query, out IntPtr vouts, out nuint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseObject(IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetVariable(IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string name, int type, long value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeMemory(IntPtr memory);
    private sealed record Api(GetInputThread InputThread, InputControl Control, ReleaseObject Release, SetVariable Set, FreeMemory Free);
    private static Api? api;
    private static bool resolved;

    // Binds to the LibVLC already loaded for playback, and only to the 3.0 series whose query number is known.
    // libvlccore allocates the list of outputs with msvcrt, so msvcrt frees it.
    private static Api? Resolve(LibVLC engine)
    {
        if (resolved) return api;
        resolved = true;
        if (!engine.Version.StartsWith("3.0.", StringComparison.Ordinal) || RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
        static IntPtr Export(string module, string name) =>
            GetModuleHandle(module) is var handle && handle != IntPtr.Zero && NativeLibrary.TryGetExport(handle, name, out var address) ? address : IntPtr.Zero;
        var exports = new[] { Export("libvlc.dll", "libvlc_get_input_thread"), Export("libvlccore.dll", "input_Control"),
            Export("libvlccore.dll", "vlc_object_release"), Export("libvlccore.dll", "var_SetChecked"), Export("msvcrt.dll", "free") };
        if (exports.Contains(IntPtr.Zero)) return null;
        return api = new(Marshal.GetDelegateForFunctionPointer<GetInputThread>(exports[0]), Marshal.GetDelegateForFunctionPointer<InputControl>(exports[1]),
            Marshal.GetDelegateForFunctionPointer<ReleaseObject>(exports[2]), Marshal.GetDelegateForFunctionPointer<SetVariable>(exports[3]),
            Marshal.GetDelegateForFunctionPointer<FreeMemory>(exports[4]));
    }

    // Sets the margin on each of the player's video outputs; false while there is none, or when this LibVLC can't.
    public static bool TrySet(LibVLC engine, MediaPlayer player, int margin)
    {
        if (Resolve(engine) is not { } vlc) return false;
        var input = vlc.InputThread(player.NativeReference);
        if (input == IntPtr.Zero) return false;
        try
        {
            if (vlc.Control(input, InputGetVouts, out var vouts, out var count) != 0) return false;
            var set = false;
            for (nuint i = 0; i < count; i++)
            {
                var vout = Marshal.ReadIntPtr(vouts, (int)i * IntPtr.Size);
                set |= vlc.Set(vout, "sub-margin", VarInteger, margin) == 0;
                vlc.Release(vout);
            }
            if (vouts != IntPtr.Zero) vlc.Free(vouts);
            return set;
        }
        finally { vlc.Release(input); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
}

public partial class MainWindow
{
    // While the dock shows, subtitles ease up to just above it, and back down when it hides. The lift is in video pixels.
    private const double SubtitleDockGap = 6;
    private double subtitleLift, subtitleLiftTarget;
    private int subtitleLiftApplied;
    private DispatcherTimer? subtitleLiftTimer;

    // The dock's place is known only once layout settles, so changes are measured after it. The playback tick also
    // measures again, as the video's size becomes known without an event.
    private void InitializeSubtitleLift()
    {
        void Queue() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RefreshSubtitleLift);
        PlaybackControls.IsVisibleChanged += (_, _) => Queue();
        PlaybackDock.SizeChanged += (_, _) => Queue();
        PlayerOverlay.SizeChanged += (_, _) => Queue();
    }
    // A new video output starts with no lift.
    private void SubtitleOutputChanged()
    {
        subtitleLift = subtitleLiftApplied = 0;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RefreshSubtitleLift);
    }
    private void RefreshSubtitleLift()
    {
        subtitleLiftTarget = SubtitleLiftTarget();
        if (subtitleLiftApplied == (int)Math.Round(subtitleLiftTarget)) { subtitleLift = subtitleLiftTarget; return; }
        if (subtitleLiftTimer is null)
        {
            subtitleLiftTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
            subtitleLiftTimer.Tick += (_, _) => StepSubtitleLift();
        }
        subtitleLiftTimer.Start();
    }
    private void StepSubtitleLift()
    {
        subtitleLift += (subtitleLiftTarget - subtitleLift) * 0.35;
        if (Math.Abs(subtitleLiftTarget - subtitleLift) < 1) subtitleLift = subtitleLiftTarget;
        var margin = (int)Math.Round(subtitleLift);
        // Without an output yet there is nothing to move; the output's arrival starts over.
        if (margin != subtitleLiftApplied)
        {
            if (closing || vlc is null || player is null || !VlcSubtitleMargin.TrySet(vlc, player, margin)) { subtitleLiftTimer!.Stop(); return; }
            subtitleLiftApplied = margin;
        }
        if (subtitleLift == subtitleLiftTarget) subtitleLiftTimer!.Stop();
    }
    // How far the dock reaches into the picture, above any black bar under it, converted from screen to video pixels.
    private double SubtitleLiftTarget()
    {
        if (player is null || pictureInPicture || !PlaybackControls.IsVisible) return 0;
        uint width = 0, height = 0;
        if (!player.Size(0, ref width, ref height) || width == 0 || height == 0) return 0;
        var frame = PlayerOverlay.RenderSize;
        if (frame.Width <= 0 || frame.Height <= 0) return 0;
        var shown = Math.Min(frame.Height, frame.Width * height / width);
        var letterbox = (frame.Height - shown) / 2;
        var covered = frame.Height - PlaybackDock.TranslatePoint(new Point(), PlayerOverlay).Y + SubtitleDockGap - letterbox;
        return covered <= 0 ? 0 : covered * height / shown;
    }
}
