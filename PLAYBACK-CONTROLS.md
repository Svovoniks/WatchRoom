# Playback controls

Windows uses a dedicated VLC-style player: a black video surface, compact charcoal transport bar, orange seek/volume tracks, elapsed/total time, and an Audio / subtitles panel. The library rail and app status bar give way to the player. Room participants and chat can be toggled from the header and start hidden for local playback. The Mac client retains its existing compact transport and expandable options.

| Key | Action |
| --- | --- |
| Space | Play / pause |
| Left / Right | Seek back / forward 10 seconds |
| Up / Down | Change volume by 5% |
| M | Mute / unmute |
| S | Stop local playback; pause and return to start in a room |
| N | Play next queued title |
| F | Toggle fullscreen |
| F11 (Windows) | Toggle fullscreen |
| Escape | Exit fullscreen |

Typing in a text field does not trigger playback shortcuts. Seek and volume sliders retain their own arrow-key behavior. Shared play, pause, seek and return-to-start commands use the room's existing host/shared-control permissions. Volume, mute and track selection affect only your player.

On Windows, fullscreen occupies the entire current monitor, including the taskbar area. It removes the title bar, borders, page padding, header, sidebar and status row. The video retains its original aspect ratio; black bars can remain for differently shaped media. Controls overlay the native video and hide after 2.6 seconds of inactivity. Move the mouse or press Tab to reveal them; controls remain visible while hovered, scrubbing, or using the options panel. The fullscreen button, F, F11, or a double-click on the video toggles fullscreen; Escape exits and restores the previous window bounds and maximized state. Mac fullscreen behavior is unchanged.

Validation: Windows desktop builds without warnings or errors. Generated-video playback, the fullscreen button, idle overlay hiding, double-click fullscreen, Escape restoration, and the audio/subtitle panel were checked in the running Windows app. All 47 existing smoke checks pass, including remote playback, seeking, room permissions and subtitle transfer. Multi-monitor/mixed-DPI fullscreen still needs testing on that hardware. Windows waits for its native video surface before starting playback to avoid a separate VLC output window. Real macOS validation remains pending.
