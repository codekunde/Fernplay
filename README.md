# Fernplay

A small music player for Windows in the style of the late-90s desktop players: a player panel with a green LCD, a 10-band equalizer and a playlist, as three panels that snap together and come apart. It also plays internet radio. Offline, no account, no library database.

Windows 10 (1607 or later) and 11, x64.

![Fernplay: player, equalizer and playlist docked together, playing a track](docs/screenshots/fernplay.png)

## The three panels

- **Player:** 7-segment clock (click it for time left), spectrum analyser, scrolling title, kbps, kHz, mono/stereo, volume (mouse wheel works too), position bar, previous / play / pause / stop / next, open (right-click it for a folder), SHUFFLE, REPEAT (off, whole list, this track), and EQ / PL switches that show or hide the other panels.
- **Equalizer:** ON switch, curve preview, preamp and 10 bands (60 Hz to 16 kHz, ±12 dB; bars turn from green to orange as they boost), PRESETS (Flat, Rock, Pop, Classical, Dance, Bass boost, Treble boost, Vocal). Changes apply live.
- **Playlist:** tracks as "03 - Title - Artist" with their length, the playing one highlighted; double-click or Enter plays, Delete or REM removes, ADD adds files or a folder, CLEAR empties it, LIST opens or saves a playlist file; total length in the corner. Drag tracks to reorder them (several at once), or use Alt+Up / Alt+Down. Drag the panel's bottom edge to show more or fewer rows.

Drag a panel by its title bar: it snaps to the other panels and to the screen edges. Dragging the player panel moves every panel docked to it; dragging the EQ or playlist alone pulls it loose.

## Opening music

- The eject button (Ctrl+O), or ADD in the playlist.
- Drop files or folders on the player (replaces the list and plays) or on the playlist (adds to the end).
- From the command line: `Fernplay.exe song.mp3 Album\` replaces the list; `Fernplay.exe /add song.mp3` adds to the end.
- From Explorer, once switched on in Settings (see below).

Only one Fernplay runs at a time: anything opened while it runs goes to the running player. Several files opened together become one list in Explorer's name order.

Plays MP3, FLAC, WAV, WMA and AAC (M4A) with Windows' own decoders. OGG and Opus are not supported yet: Windows has no decoder for them. A file that cannot be played is skipped and the reason scrolls through the title display.

On a Windows "N" edition, install Microsoft's Media Feature Pack first: without it Windows has no audio decoders.

### Playlist files

LIST opens and saves `.m3u` and `.m3u8`; `.pls` opens too. Playlists also open from the open dialog, by drag and drop and from the command line. Entries inside the playlist's folder are saved relative to it. Missing or unplayable entries are left out, and the title display says how many.

## Internet radio

![Fernplay playing a radio station, with the song title from the stream and three stations in the playlist](docs/screenshots/radio.png)

Stream addresses (`http://` or `https://`) in a `.m3u`, `.m3u8` or `.pls` file, or on the command line, play as live stations, marked LIVE in the playlist:

- The station's name comes from the playlist; if it has none, Fernplay uses the name the station sends and remembers it.
- MP3 stations that send the song in the stream show "Artist - Title (Station)" in the title display, the window title and the Windows media overlay. The title changes when the song changes on the speakers, not when the data arrives. Not every station sends song titles.
- The seek bar is off and the clock counts up. Stop disconnects, Play connects again.
- A station that drops or goes quiet for 15 seconds reconnects (up to three times a minute). An unreachable station says so instead of skipping to the next entry.

## Keys

| Key | Does |
|---|---|
| Z X C V B | Previous, play, pause, stop, next (in every panel) |
| Space | Play / pause |
| Left / Right | Seek 5 s back or forward (player panel) |
| Up / Down | Volume (player panel) |
| Ctrl+O | Open files |
| Enter, Delete | Play or remove the selected tracks (playlist) |
| Alt+Up / Alt+Down | Move the selected tracks (playlist) |
| Esc | Close Settings or About |

**Media keys and the Windows media overlay.** Fernplay registers with Windows' media session, so the keyboard's play/pause, stop, next and previous keys work while other apps have focus, and Windows' media overlay (next to the volume flyout) shows the track with its buttons.

If the media keys do nothing while Fernplay plays, another program has taken them first. Winamp, for example, catches them with its global hotkeys while it runs: close it, or switch off its multimedia keys under Preferences, Global Hotkeys.

## Settings

![Fernplay settings: keep on top, snap panels, remember the playlist, open from Explorer](docs/screenshots/settings.png)

Open them from the Fernplay icon at the top left of the player (Settings…, About Fernplay, Exit).

- **Keep Fernplay on top** of other windows.
- **Snap panels** to each other and to the screen edges.
- **Remember the playlist** between runs.
- **Open music and playlists from Explorer** (off by default, for the current user only, no admin rights): adds Fernplay to Open with and Default apps, gives the files it plays its file icon, and adds "Add to Fernplay playlist" (files and folders) and "Play in Fernplay" (folders) to the right-click menu. Untick it to remove everything again. **Make Fernplay the default player…** then opens Windows' own page for the choice.

Fernplay remembers the panel positions, which panels are shown, volume, EQ, shuffle, repeat, the playlist height, the playlist and station names in `%APPDATA%\CodeKunde\Fernplay\settings.json`. The `FERNPLAY_DATA` environment variable points it at another folder.

## Build

Needs the .NET 10 SDK.

```
dotnet build Fernplay.slnx
dotnet test Fernplay.slnx
dotnet publish src/Fernplay.App -c Release -o publish   # publish\Fernplay.exe
```

The published exe (about 1.8 MB) is framework-dependent: it needs the .NET 10 Desktop Runtime on the PC.

App and file icons are drawn by `tools/Make-FernplayIcons.ps1`.

## How it is built

Three parts in a chain, each replaceable on its own: **decoder → EQ → output** (plus a spectrum tap and the volume).

| Folder | What |
|---|---|
| `src/Fernplay.Core` | No UI. `PlayerEngine` (load in the background, play, pause, stop, seek, volume, track end, stream titles), `Audio/` (`MediaFoundationDecoder`; `IcyMp3Decoder`, `IcyStream` and `IcyMetadata` for radio song titles; `Equalizer` and `EqualizerStage` (RBJ peaking biquads), `SpectrumTap` (FFT), `GainStage`, `WasapiAudioOutput`), `Playlist` (shuffle, repeat, remove, move, opening playlists), `M3u` and `Pls`, `PanelLayout` (snapping and docking maths), `PlaylistHeight`, `Track` and `TagReader` (Windows property system), `ShellRegistration` (Explorer entries under HKCU), `OpenRequest`, `PlaybackErrors`, `TimeFormat` |
| `src/Fernplay.App` | `Fernplay.exe`, WPF: `Skin.xaml` (palette, buttons, sliders, menus, scroll bar, glyphs; all drawn, no bitmaps), `SkinControls` (7-segment clock, spectrum, marquee, EQ curve), `SkinWindow` (dragging, snapping), `MainWindow`, `EqWindow`, `PlaylistWindow`, `SettingsWindow`, `AboutWindow`, `PlayerController`, `MediaSession` (Windows media session through COM, no WinRT package), `SingleInstance`, `ShellNotify`, `FocusRing`, `UserSettings` |
| `tests/Fernplay.Tests` | xUnit tests; they write their own audio and use a fake output, so they need no sound card or network |

Audio: [NAudio](https://github.com/naudio/NAudio) 3.2 (MIT), Media Foundation for decoding, Windows' MP3 codec (ACM) for radio stations with song titles, WASAPI shared mode for output (follows the default device when it changes). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The look follows the classic skinned players in spirit; every part of it is Fernplay's own drawing. The screenshots show made-up demo tracks and a local test station.

MIT licence. Copyright (c) 2026 CodeKunde.
