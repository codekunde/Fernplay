# Changelog

## Unreleased

- Visualizer: the app menu's Visualizer (MilkDrop3)… starts MilkDrop3, a separate free program (download it from github.com/milkdrop2077/MilkDrop3) that shows full-screen visuals for whatever the PC plays. The first time, point Fernplay at `MilkDrop 3.exe` in MilkDrop3's folder; it remembers the place. A visualizer inside Fernplay is planned.
- Settings has two tabs: GENERAL (on top, snap, remember the playlist, Explorer, skin) and ADVANCED (crossfade length, volume levelling, phone remote). The window is about 40 % shorter and keeps its height when you switch tabs.
- Volume levelling (ReplayGain): Settings → ADVANCED → VOLUME LEVELLING plays every song (TRACK) or every album (ALBUM) at the same loudness, using the ReplayGain tags in MP3 (ID3v2 and APEv2), FLAC and M4A files. Off by default. Fernplay reads the tags but does not write them: a tagger such as foobar2000, MusicBee or rsgain does. While levelling is on, files without tags and radio stations play 6 dB quieter, so they sit close to tagged music. A gain never pushes a song's tagged peak over full scale. Changing the setting applies to the playing song at once, and a crossfade keeps each song at its own level.
- Phone remote (desktop side): Settings → ADVANCED → PHONE REMOTE lets the Fernplay app on your phone control this player over the home Wi-Fi: play, pause, stop, skip, seek, volume, shuffle, repeat, crossfade, and pick from the playlist. Off by default. PAIR A PHONE… shows a 6-digit code for 2 minutes; paired phones are listed with REMOVE. The link is encrypted (TLS with the PC's own certificate, which the phone pins at pairing) and only addresses on the local network are served. Windows Firewall asks once when it is first switched on. The Android side follows in the Fernplay for Android app.
- Update from About: when CHECK FOR UPDATE finds a newer release, the installed Fernplay (from the MSI) offers UPDATE. It downloads the release's MSI, checks it against the SHA-256 checksum GitHub lists for it, closes Fernplay, installs the update (progress bar only, no admin prompt) and starts Fernplay again. Portable and zip copies keep DOWNLOAD, which opens the release page. Closing About stops the download.
- Crossfade: XFADE next to REPEAT (and Crossfade in the app menu, also with classic skins) fades the end of a song into the next one, 1 to 12 seconds (Settings → ADVANCED, default 5). Only when a song ends by itself: skipping by hand, stations, and repeat track never fade, and neither do songs shorter than twice the fade.
- Gapless: songs now follow each other without a gap, on one sound stream that stays open (the next file is opened a few seconds before the end). Repeat track loops without a gap.
- Files with more than two channels (5.1) are mixed down to stereo.
- An isolated test run (`FERNPLAY_DATA` set) no longer moves the Explorer set-up to its own copy of Fernplay.
- Fixed: a name with an underscore lost it and showed an underlined letter instead, and screen readers read it the same wrong way: folders in the media library's FOLDERS menu (My_Music) and your own presets in the equalizer's PRESETS menu (my_rock).

## 0.8.0 (2026-11-06)

Windows 10 (1607 or later) and 11, x64. The MSI and the standalone zip have .NET inside; the small zip needs the .NET 10 Desktop Runtime.

- Fixed: on some Windows 11 PCs the next song started paused, and a double-click in the playlist sometimes paused the song it started. Windows sent a media-button Pause just after the song started, with nothing pressed. A media-button Pause in the first 3 seconds of a song is now ignored (and noted in the diagnostics log).
- Playlist (Fernplay's look): dragging the bottom edge snaps it level with the bottom of the equalizer or another panel. The list still shows whole rows; the few spare pixels go under it.
- Playlist: resizing can reach the whole desktop (other screens included) and never shrinks below the size the drag started at. In Fernplay's look the corner grip also changes the width (the player's width at least), and the width is remembered.
- Repeat: a click turns repeating the whole list on or off; a right-click (or the menu key, or Shift+F10) turns repeating this track on or off. The tooltip says which is on. The old three-step cycle looked the same for list and track on classic skins.
- Equalizer: Presets has Save current as… and Delete preset › for your own presets, kept with the settings.
- Classic skin sizes are now 1x, 1.5x and 1.75x (were 1x, 2x and 3x). At 1x on 100–125 % displays the playlist text is larger and easier to read.

## 0.7.0 (2026-11-05)

Windows 10 (1607 or later) and 11, x64. The MSI and the standalone zip have .NET inside; the small zip needs the .NET 10 Desktop Runtime.

- The mouse wheel over the position bar moves through the track, 5 seconds per notch (up = forward), like the wheel over the volume bar. Also on the shade strip and with classic skins; not on a radio station.
- Installer: `Fernplay-<version>-win-x64.msi` installs Fernplay for the current Windows account (no admin prompt, .NET inside) with a Start menu entry, and sets it up in Explorer ("Open with" for music files and playlists, "Play in Fernplay" and "Add to Fernplay playlist"). A newer MSI updates in place and keeps the settings. Unticking the Explorer option in Settings stays off through later updates. Uninstalling also removes the Explorer set-up.
- Portable: the zips now carry `portable.txt`, which keeps settings, library and skins in a `Data` folder next to `Fernplay.exe`. The first start copies them from an installed Fernplay. Without the file, settings stay in `%APPDATA%\CodeKunde\Fernplay` as before.
- `tools\Build-Release.ps1` builds the MSI and both zips into `dist\<version>`.

## 0.6.0 (2026-11-04)

Same requirements as 0.1.0: Windows 10 (1607 or later) and 11, x64, with the .NET 10 Desktop Runtime.

- About: CHECK FOR UPDATE asks GitHub for the newest release and says whether there is one; DOWNLOAD then opens its page. Nothing is checked unless the button is pressed.
- A double-click on an equalizer slider (bands and preamp) sets it back to 0 dB; a double-click on the balance sets it back to the centre. Also with classic skins.
- Fixed: on Windows 11 a click on Fernplay's taskbar button did not minimise it (Windows 10 did).
- Fixed: after Windows was locked, or a screen went to sleep or was unplugged, the player, equalizer and playlist could come back apart. Fernplay now remembers where the panels were and puts them back once the screens are as before.
- Radio: a station that can't be reached now says why instead of always "the station is offline": no answer, connection refused or blocked (with a hint that a firewall or security program is blocking Fernplay.exe when the station plays in a browser), secure (https) connection failed, address not found, proxy refused, or the station's own answer (404 moved, 403 refused). Anything else shows Windows' own message and code.
- Radio: a station whose https address hands on to a plain http server now plays (browsers follow that step; Fernplay did not).
- A small diagnostics log (`diagnostics.log` next to the settings, at most about 512 KB, never sent anywhere) notes play and pause with what asked for them, Windows media buttons, track ends, lock and unlock, and display changes, so faults that happen now and then can be traced.

## 0.5.0 (2026-11-03)

Same requirements as 0.1.0: Windows 10 (1607 or later) and 11, x64, with the .NET 10 Desktop Runtime.

- File info: Alt+3 (as on the classic player) edits a track's tags: title, artist, album, album artist, track number, year, genre, composer and comment. From the playlist and the library (selected track, also on the right-click menu and a classic skin's MISC button) or the player (the playing track). Only changed fields are written, through Windows' own tag support (MP3, M4A, WMA; other formats where Windows can write them). The playlist, the player and the library show the new tags at once. The playing track keeps playing from the same place. WAV, read-only files and files open in another program are explained, not changed.
- Playlist: the playing track's length is white like its title (it stayed green in Fernplay's own look).
- Playlist: Delete no longer jumps the list to the top or to the playing track. The view stays where it was, and the selection and keyboard focus move to the next track, so Delete or the arrow keys go on from there.

## 0.4.1 (2026-11-01)

Same requirements as 0.1.0: Windows 10 (1607 or later) and 11, x64, with the .NET 10 Desktop Runtime.

- Fixed: after Windows was locked while Fernplay was minimised, the player came back larger than its picture, with black areas and shifted away from the equalizer and playlist (seen on Windows 10 and 11). The panels now fit their content again on restore, unlock, wake from sleep and display changes.

## 0.4.0 (2026-10-31)

Same requirements as 0.1.0: Windows 10 (1607 or later) and 11, x64, with the .NET 10 Desktop Runtime.

- Windowshade: the shade button on the title bar, a double-click on the title bar or Ctrl+W rolls the player up to a strip as tall as its title bar. In Fernplay's look the strip shows the scrolling song title with the play state, the time (click to switch between played and left), mini transport and eject, and a small position bar that seeks; with a classic skin it is the skin's own strip with its mini spectrum, time, transport and position bar. The same again, or a double-click on the strip, rolls it down. Panels docked below move with it, the library beside it keeps level, and Fernplay remembers it for the next start.
- Fernplay's own look shows which panel has the keyboard: its title bar is lit, the other panels' title bars are dimmed (classic skins already did this with the skin's own title pictures).
- Library: Esc in the empty search box hands the keyboard back to the track list, so the player keys (Z X C V B, Space) work again without a click. Esc with text in the box still clears it first.

## 0.3.0 (2026-10-31)

Same requirements as 0.1.0: Windows 10 (1607 or later) and 11, x64, with the .NET 10 Desktop Runtime.

- Media library, in its own window (app menu "Media library…" or Alt+L). Optional: nothing is scanned or saved until a folder is added, and the player, equalizer and playlist work as before without it.
- Folders: FOLDERS adds a music folder (scanned in the background), rescans, stops a scan, or removes a folder from the library (files stay on disk). Fernplay rescans at start; only new or changed files are read again. A folder on a drive that is not plugged in keeps its tracks.
- Views: Audio, Most Played, Recently Added, Recently Played, Never Played, Top Rated, the playlist files found in the folders (stations show as LIVE, missing entries are counted), and History (each play, newest first; Delete removes plays, "Clear history…" empties it). A play counts after half the track or four minutes.
- Search as you type (every word must match; accents ignored; Ctrl+F, Esc clears), artist and album panes, sortable columns (Artist, Album, #, Title, Time, Genre, Year, Rating, Plays, Last played). PLAY replaces the playlist, ENQUEUE adds to it (the selected tracks, or all shown); double-click and Enter play, Shift+Enter enqueues; drag tracks to the playlist or the player. A status line shows items, total time, size and query time.
- Missing tags are filled from folder and file names ("Artist\Album\01 - Title", "01 - Artist - Title"); real tags win.
- Covers: the art inside the file, else folder.jpg, cover.jpg, front.jpg or AlbumArt pictures in its folder.
- Ratings: click the stars (click the same star again to clear), press 0 to 5, or use the right-click menu. Ratings are kept in Fernplay's library only, never written to music files. The right-click menu also has Play, Enqueue and Show in Explorer.
- The library window docks beside the player, as tall as the player's panels together, and stays so when the skin or its size changes, until it is resized with its corner grip. Place, size and whether it was open are remembered.
- Classic skins: the library is drawn from the skin's GEN.BMP (frame, title in the skin's letters, close button) and GENEX.BMP (colours of lists, selection, headers, buttons, scroll bars). Its text stays at Fernplay's size.
- The library is kept in `library.json` next to the settings.

## 0.2.0 (2026-10-31)

Same requirements as 0.1.0: Windows 10 (1607 or later) and 11, x64, with the .NET 10 Desktop Runtime.

- Classic skins: Fernplay reads classic skin files (`.wsz`, the 2.x format) and draws the player, equalizer and playlist from them, pixel-sharp at 1x, 2x or 3x. Choose a skin in Settings (SKIN: CHOOSE SKIN…, FERNPLAY LOOK, SIZE) or drop a `.wsz` on the player; it is copied to the skins folder next to the settings and remembered. Fernplay ships no skins; its own look stays the default. A file that is not a usable skin says why in the title display.
- Skinned equalizer: preamp and 10 band sliders whose colour follows the gain, ON, PRESETS and the curve graph in the skin's colours; AUTO is drawn but not active yet. Panels docked below move when the skin changes the equalizer's height.
- Skinned playlist: the skin's frame, colours and font (PLEDIT.TXT), scroll knob and arrows, running time (selected/total), mini transport and clock. Button menus as on the classic player: ADD (folder, files; stream address not yet), REM (all, crop, selected, missing files), SEL (invert, none, all), MISC (drawn, not active yet), LIST (new, save, open). The bottom-right corner drags the height in 29 px steps and the width in 25 px steps (from 275; the skin's visualizer panel shows from 350), remembered. Drag-reorder, keys and screen-reader names work as in Fernplay's look.
- Balance: a BAL slider on the player in Fernplay's own look and on classic skins, sharing one setting. Within 15 % of the centre it snaps there on a click, a key or when let go; the mouse wheel and arrow keys move it 10 % per notch, and a step away from the centre goes straight to 20 %.
- Fernplay's own look: title bars show three thin, evenly spaced bars instead of fine grooves, sharp at 100 % scaling.
- Space is always play/pause, also right after clicking a button (it used to press the clicked button again, so clicking Play and then Space restarted the track). Enter presses the focused button.
- README: classic skins section, Space and Enter in the keys table, new screenshots of Fernplay's own look.
- Track names read "03 - Artist - Title", or "Artist - Title" without a track number. A number written into the title tag ("03 - Title") moves to the front instead of showing twice; a title such as "1979" keeps its digits.

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
