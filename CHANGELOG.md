# Changelog

## Unreleased

- Classic skins (in progress: the player and equalizer panels so far): Fernplay reads classic skin files (`.wsz`, the 2.x format) and draws the player from them, pixel-sharp at 1x, 2x or 3x. Choose a skin in Settings (SKIN: CHOOSE SKIN…, FERNPLAY LOOK, SIZE) or drop a `.wsz` on the player; it is copied to the skins folder next to the settings and remembered. Fernplay ships no skins; its own look stays the default. A file that is not a usable skin says why in the title display.
- Skinned equalizer: preamp and 10 band sliders whose colour follows the gain, ON, PRESETS and the curve graph in the skin's colours; AUTO is drawn but not active yet. Panels docked below move when the skin changes the equalizer's height.
- Balance: a BAL slider on the player in Fernplay's own look and on classic skins, sharing one setting. Within 15 % of the centre it snaps there on a click, a key or when let go; the mouse wheel and arrow keys move it 10 % per notch, and a step away from the centre goes straight to 20 %.
- Fernplay's own look: title bars show three thin, evenly spaced bars instead of fine grooves, sharp at 100 % scaling.
- Space is always play/pause, also right after clicking a button (it used to press the clicked button again, so clicking Play and then Space restarted the track). Enter presses the focused button.

## 0.1.0 (2026-10-30)

First release. Windows 10 (1607 or later) and 11, x64; needs the .NET 10 Desktop Runtime.

- Classic skinned look, drawn in code: player, equalizer and playlist panels that snap to each other and the screen edges, move together when docked to the player and come loose when dragged alone; EQ and PL switches show or hide them.
- Player panel: 7-segment clock (played or left, blinks while paused), spectrum analyser, scrolling title (also shows errors), kbps, kHz, mono/stereo, volume, position bar, previous / play / pause / stop / next, open files or a folder, shuffle, repeat (off, list, track).
- Equalizer: 10 bands at the classic centres (60 Hz to 16 kHz, ±12 dB) plus preamp, on/off, curve preview, eight presets; changes apply live without clicks.
- Playlist: tracks with lengths read in the background, playing track highlighted, double-click or Enter to play, Delete or REM to remove, ADD files or a folder, CLEAR, drag and drop to add; total length, with radio stations counted apart ("6 tracks  19:09 + 3 stations").
- Amber title bars and menus; the skin is drawn at 1.7x (476 px wide).
- App icon and a file icon for audio files and playlists (drawn by `tools/Make-FernplayIcons.ps1`).
- App menu (icon at the top left of the player): Settings (keep on top, snap panels, remember the playlist), About, Exit.
- Playlists: open and save `.m3u` / `.m3u8` (LIST button); playlists also open from the open dialog, drag and drop and the command line. Entries are stored relative to the playlist's folder; missing or unplayable entries are left out and counted in the title display.
- Reorder the playlist by dragging tracks (several at once, with an insertion line and scrolling at the edges) or with Alt+Up / Alt+Down.
- Track names read "03 - Title - Artist" (track number from the tag when the title does not already start with one); the playlist has its own scroll bar beside the text.
- Keys: Z X C V B = previous, play, pause, stop, next and Space = play/pause in every panel; Left/Right seek 5 s and Up/Down set the volume on the player; media keys work while other apps have focus, and the Windows media overlay shows the track with play, pause, stop, next and previous (Windows' media session, through a small built-in bridge, no WinRT package). The mouse wheel over the volume bar sets the volume. The keyboard focus ring shows only while the keyboard is in use.
- Explorer: a Settings switch (off by default, current user only, no admin) adds Fernplay to Open with and Default apps, its file icon to the types it plays, and "Add to Fernplay playlist" / "Play in Fernplay" to the right-click menu; a button opens Windows' page to make Fernplay the default player. Unticking removes everything again.
- One Fernplay at a time: files opened while it runs go to the running player. Several files opened together become one list in Explorer's name order; `/add` adds to the end.
- The playlist panel's bottom edge can be dragged to show more or fewer tracks (whole rows, at least four, never past the bottom of the screen); the height is remembered.
- Internet radio: stream addresses in `.m3u`, `.m3u8` and `.pls` playlists (and on the command line) play as live stations under their playlist name, marked LIVE. Connecting runs in the background with "Connecting to …" in the title display; a newer choice takes over; the seek bar is off (its knob stays at the start) and the clock counts up; Stop disconnects and Play connects again; a dropped station reconnects up to three times a minute; an unreachable station says so instead of skipping on. Stations and their names are remembered and saved in playlists. `.pls` is added to the Explorer file types.
- Radio song titles: MP3 stations that send the song inside the stream (ICY) show "Artist - Title (Station)" in the title display and the window title, and title and artist in the Windows media overlay; the title changes when the song changes on the speakers, not when the data arrives. The kbps box shows the station's bitrate. A station without a name in its playlist takes the name it sends (for example "hr3 Rhein-Main"), and it is remembered. These stations are read by Fernplay itself and decoded by Windows' MP3 codec; other stations play through Media Foundation as before. A station that goes quiet for 15 seconds counts as dropped and reconnects.
- Remembers panel positions and visibility, volume, EQ, shuffle, repeat and the playlist between runs.
- Core: playback chain decoder → EQ → spectrum tap → volume → output; Media Foundation decoding (MP3, FLAC, WAV, WMA, AAC), WASAPI output that follows the default device; tags through the Windows property system; unplayable files are skipped with a reason.
- README with screenshots; framework-dependent single exe (about 1.8 MB).
