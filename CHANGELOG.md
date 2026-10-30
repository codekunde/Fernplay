# Changelog

## 0.1.0 (unreleased)

- Classic skinned look, drawn in code: player, equalizer and playlist panels that snap to each other and the screen edges, move together when docked to the player and come loose when dragged alone; EQ and PL switches show or hide them.
- Player panel: 7-segment clock (played or left, blinks while paused), spectrum analyser, scrolling title (also shows errors), kbps, kHz, mono/stereo, volume, position bar, previous / play / pause / stop / next, open files or a folder, shuffle, repeat (off, list, track).
- Equalizer: 10 bands at the classic centres (60 Hz to 16 kHz, ±12 dB) plus preamp, on/off, curve preview, eight presets; changes apply live without clicks.
- Playlist: numbered list with lengths read in the background, playing track highlighted, double-click or Enter to play, Delete or REM to remove, ADD files or a folder, CLEAR, drag and drop to add; total length.
- Remembers panel positions and visibility, volume, EQ, shuffle, repeat and the playlist between runs.
- Core: playback chain decoder → EQ → spectrum tap → volume → output; Media Foundation decoding (MP3, FLAC, WAV, WMA, AAC), WASAPI output that follows the default device; tags through the Windows property system; unplayable files are skipped with a reason.
