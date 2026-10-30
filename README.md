# Fernplay

A small music player for Windows in the style of the late-90s desktop players: a player panel with a green LCD, a 10-band equalizer and a playlist, as three panels that snap together and come apart. Offline, no account, no library database.

Part of the CodeKunde Toolbox. Windows 10 (1607 or later) and 11, x64.

## The three panels

- **Player:** 7-segment clock (click it for time left), spectrum analyser, scrolling title, kbps, kHz, mono/stereo, volume, position bar, previous / play / pause / stop / next, open (right-click it for a folder), SHUFFLE, REPEAT (off, whole list, this track), and EQ / PL switches that show or hide the other panels.
- **Equalizer:** ON switch, curve preview, preamp and 10 bands (60 Hz to 16 kHz, ±12 dB; bars turn from green to orange as they boost), PRESETS (Flat, Rock, Pop, Classical, Dance, Bass boost, Treble boost, Vocal).
- **Playlist:** numbered tracks with their length, the playing one highlighted; double-click or Enter plays, Delete or REM removes, ADD adds files or a folder, CLEAR empties it; total length in the corner.

Drag a panel by its title bar: it snaps to the other panels and to the screen edges. Dragging the player panel moves every panel docked to it; dragging the EQ or playlist alone pulls it loose.

Fernplay remembers the panel positions, which panels are shown, volume, EQ, shuffle, repeat and the playlist in `%APPDATA%\CodeKunde\Fernplay\settings.json`.

## Opening music

Use the eject button (Ctrl+O), drop files or folders on the player (replaces the list and plays) or on the playlist (adds to the end), or start `Fernplay.exe song.mp3 Album\` (works with Explorer's "Open with").

Plays MP3, FLAC, WAV, WMA and AAC (M4A) with Windows' own decoders. OGG and Opus are not supported yet: Windows has no decoder for them. A file that cannot be played is skipped and the reason scrolls through the title display.

On a Windows "N" edition, install Microsoft's Media Feature Pack first: without it Windows has no audio decoders.

## Build

Needs the .NET 10 SDK.

```
dotnet build Fernplay.slnx
dotnet test Fernplay.slnx
dotnet publish src/Fernplay.App -c Release -o publish   # publish\Fernplay.exe
```

The published exe is framework-dependent: it needs the .NET 10 Desktop Runtime on the PC.

## How it is built

Three parts in a chain, each replaceable on its own: **decoder → EQ → output** (plus a spectrum tap and the volume).

| Folder | What |
|---|---|
| `src/Fernplay.Core` | No UI. `PlayerEngine` (load, play, pause, stop, seek, volume, track end), `Audio/` (`MediaFoundationDecoder`, `Equalizer` and `EqualizerStage` (RBJ peaking biquads), `SpectrumTap` (FFT), `GainStage`, `WasapiAudioOutput`), `Playlist` (shuffle, repeat, remove), `PanelLayout` (snapping and docking maths), `Track` and `TagReader` (Windows property system), `PlaybackErrors`, `TimeFormat` |
| `src/Fernplay.App` | `Fernplay.exe`, WPF: `Skin.xaml` (palette, buttons, sliders, glyphs; all drawn, no bitmaps), `SkinControls` (7-segment clock, spectrum, marquee, EQ curve), `SkinWindow` (dragging, snapping), `MainWindow`, `EqWindow`, `PlaylistWindow`, `PlayerController`, `UserSettings` |
| `tests/Fernplay.Tests` | xUnit tests; they write their own audio and use a fake output, so they need no sound card |

Audio: [NAudio](https://github.com/naudio/NAudio) 3.2 (MIT), Media Foundation for decoding, WASAPI shared mode for output (follows the default device when it changes). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The look follows the classic skinned players in spirit; every part of it is Fernplay's own drawing.

MIT licence. Copyright (c) 2026 CodeKunde.
