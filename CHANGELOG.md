# Changelog

## 0.1.0 (unreleased)

- Classic skinned look, drawn in code: player, equalizer and playlist panels that snap to each other and the screen edges, move together when docked to the player and come loose when dragged alone; EQ and PL switches show or hide them.
- Player panel: 7-segment clock (played or left, blinks while paused), spectrum analyser, scrolling title (also shows errors), kbps, kHz, mono/stereo, volume, position bar, previous / play / pause / stop / next, open files or a folder, shuffle, repeat (off, list, track).
- Equalizer: 10 bands at the classic centres (60 Hz to 16 kHz, ±12 dB) plus preamp, on/off, curve preview, eight presets; changes apply live without clicks.
- Playlist: numbered list with lengths read in the background, playing track highlighted, double-click or Enter to play, Delete or REM to remove, ADD files or a folder, CLEAR, drag and drop to add; total length.
- Amber title bars and menus; the skin is drawn at 1.7x (476 px wide).
- App icon and a file icon for audio files and playlists (drawn by `tools/Make-FernplayIcons.ps1`).
- App menu (icon at the top left of the player): Settings (keep on top, snap panels, remember the playlist), About, Exit.
- Playlists: open and save `.m3u` / `.m3u8` (LIST button); playlists also open from the open dialog, drag and drop and the command line. Entries are stored relative to the playlist's folder; missing or unplayable entries are left out and counted in the title display.
- Reorder the playlist by dragging tracks (several at once, with an insertion line and scrolling at the edges) or with Alt+Up / Alt+Down.
- Track names read "03 - Title - Artist" (track number from the tag when the title does not already start with one); the playlist has its own scroll bar beside the text.
- Keys: Z X C V B = previous, play, pause, stop, next and Space = play/pause in every panel; Left/Right seek 5 s and Up/Down set the volume on the player; media keys work while other apps have focus, and the Windows media overlay shows the track with play, pause, stop, next and previous (Windows' media session, through a small built-in bridge; the exe stays 1.6 MB). The keyboard focus ring shows only while the keyboard is in use.
- Remembers panel positions and visibility, volume, EQ, shuffle, repeat and the playlist between runs.
- Core: playback chain decoder → EQ → spectrum tap → volume → output; Media Foundation decoding (MP3, FLAC, WAV, WMA, AAC), WASAPI output that follows the default device; tags through the Windows property system; unplayable files are skipped with a reason.
