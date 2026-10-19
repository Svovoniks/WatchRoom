# Playback controls

Windows uses a dedicated player whose bars float over the video in the window and in fullscreen. The top bar holds the back button, the title, a room status chip (with the host's shared-controls toggle), and Room library, Room & chat (with an unread count), Copy invite and Leave. The bottom bar has a full-width timeline; Play, Stop, Previous and Next with elapsed/total time; the Audio, Subtitles and Queue pills, which show the current choice with a muted detail (for example "English 5.1" or "Season 2 3 / 8") and open their menu above the bar; and mute, volume and fullscreen. Previous and Next stay in the bar and are disabled when there is nothing to step to. A menu grows out of its pill and folds back into it when closed; clicking another pill while one is open switches to that menu, and only one menu is open at a time. When a menu changes height (switching between episodes and a queue), it eases to the new size above the pill. Narrow windows drop the pill details first, then the volume slider, then the time. While the room panel is closed, new chat appears briefly as bubbles at the top right.

The queue menu starts with a two-way switch. Episodes (or Off for a movie) turns the queue off, and Previous and Next step through the playing show's season. Queue brings back the last queue used, or shows the queue picker (without opening it) so you can choose one; it can also start a new queue from the playing season. Switching never interrupts the playing video. When the chosen queue contains it, the queue continues from there; otherwise Next starts the queue from its first video.

| Key | Action |
| --- | --- |
| Space | Play / pause |
| Left / Right | Seek back / forward 10 seconds |
| Up / Down | Change volume by 5% |
| M | Mute / unmute |
| S | Stop local playback; pause and return to start in a room |
| N | Play next queued title or episode |
| P | Play previous queued title or episode |
| F | Toggle fullscreen |
| F11 (Windows) | Toggle fullscreen |
| Escape | Exit fullscreen |

Typing in a text field does not trigger playback shortcuts. Seek and volume sliders retain their own arrow-key behavior. Shared play, pause, seek and return-to-start commands use the room's existing host/shared-control permissions. Volume, mute and track selection affect only your player.

On Windows, fullscreen occupies the entire current monitor, including the taskbar area. It removes the title bar, borders, page padding, header, sidebar and status row. The video retains its original aspect ratio; black bars can remain for differently shaped media. Controls hide after 2.6 seconds of inactivity, in the window only while a video is playing. Move the mouse or press Tab to reveal them; controls remain visible while hovered, scrubbing, or while a player menu is open. The fullscreen button, F, F11, or a double-click on the video toggles fullscreen; Escape exits and restores the previous window bounds and maximized state. Mac fullscreen behavior is unchanged.

Validation: Windows desktop builds without warnings or errors. Generated-video playback, the fullscreen button, idle overlay hiding, double-click fullscreen, Escape restoration, and the audio/subtitle panel were checked in the running Windows app. All 47 existing smoke checks pass, including remote playback, seeking, room permissions and subtitle transfer. Multi-monitor/mixed-DPI fullscreen still needs testing on that hardware. Windows waits for its native video surface before starting playback to avoid a separate VLC output window.
