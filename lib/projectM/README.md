# projectM for Fernplay's visualizer

Native DLLs the visualizer loads from next to `Fernplay.exe`. They are committed so that building Fernplay needs no
C++ tools; `tools/Build-ProjectM.ps1` rebuilds them.

| File | What | Licence |
|---|---|---|
| `win-x64/projectM-4.dll` | libprojectM 4.1.8, unmodified (with projectm-eval, hlslparser, GLM, SOIL2 built in) | LGPL 2.1 (parts MIT, Apache 2.0, public domain) |
| `win-x64/projectM-4-playlist.dll` | libprojectM 4.1.8's preset playlist library | LGPL 2.1 |
| `win-x64/glew32.dll` | GLEW 2.2.0, the release's own x64 build | Modified BSD / MIT |
| `licenses/*` | the licence texts, shipped in a `licenses` folder next to the exe | |

## Build

`tools/Build-ProjectM.ps1` (Visual Studio 2019 Build Tools or newer with C++, Windows 10 SDK) downloads and checks:

| Download | SHA-256 |
|---|---|
| `libprojectM-4.1.8.tar.gz` (github.com/projectM-visualizer/projectm, release v4.1.8) | `F72135E8B8E5FD807F908537255B41AC7BA4830309265E830CB756535487239E` |
| `glew-2.2.0-win32.zip` (github.com/nigels-com/glew, release glew-2.2.0) | `EA6B14A1C6C968D0034E61FF6CB242CFF2CE0EDE79267A0F2B47B1B0B652C164` |
| `cmake-3.31.6-windows-x86_64.zip` (projectM needs CMake 3.21+) | `D163CD3AB4959B0A53FA8988F2DDBD2E6C501658201E6A154386BAD9DBE4F836` |
| `LICENSE-2.0.txt` (apache.org) | `CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30` |

Settings, and why:
- **Shared GLEW** (`glew32.dll`). projectM never calls `glewInit`; Fernplay does, after making its OpenGL context
  current and before `projectm_create`. With GLEW linked statically into projectM no host can do that, and
  `projectm_create` calls a null GL function pointer (an access violation).
- **Static C runtime** (`CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded`): the DLLs import only `OPENGL32`, `KERNEL32` and
  `glew32.dll`, so no Visual C++ redistributable is needed.
- **`/FIiterator`**: `TextureManager.cpp` uses `std::back_inserter` without including `<iterator>`, which MSVC 14.29
  does not pull in by itself. A compiler flag, not a source change.

The DLLs are not bit-for-bit reproducible (MSVC stamps them); sizes stay the same: 1,272,320, 167,424 and 464,896 bytes.

Measured (spike, 2026-11-12, GTX 1060, `spike/projectm` branch): 60 fps at about 3 ms per frame at 784x411 and 8.6 ms
(95th percentile) at 2560x1440; 541 of Winamp 5.6.6's 552 MilkDrop 2 presets load (the rest are skipped).
