# Third-party notices

Fernplay is MIT-licensed, Copyright (c) 2026 CodeKunde. It uses the following open-source software.

| Library | Used by | Licence |
|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) (NAudio.Core, NAudio.Wasapi, NAudio.WinMM 3.2.0), © Mark Heath | decoding through Media Foundation, WASAPI output, MP3 decoding for radio stations through Windows' ACM codec | MIT License |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) 10.0.13, © .NET Foundation and Contributors | keeps the phone remote's certificate key and phone keys readable only by the Windows user (DPAPI) | MIT License |
| [Webamp](https://github.com/captbaritone/webamp), © Jordan Eldredge | the sprite positions and skin-font character table in `src/Fernplay.Core/Skins/SpriteMap.cs` and `SkinFont.cs` follow Webamp's `skinSprites.ts` (no code copied) | MIT License |
| [libprojectM](https://github.com/projectM-visualizer/projectm) 4.1.8, © the projectM Team | the visualizer (`projectM-4.dll`, `projectM-4-playlist.dll`) | GNU LGPL 2.1 |
| [GLEW](https://github.com/nigels-com/glew) 2.2.0, © Milan Ikits, Marcelo E. Magallon, Lev Povalahev; Brian Paul; The Khronos Group Inc. | OpenGL functions for projectM (`glew32.dll`) | Modified BSD, MIT (Mesa 3-D), MIT (Khronos) |
| Built into `projectM-4.dll`: [projectm-eval](https://github.com/projectM-visualizer/projectm-eval) © the projectM Team; [hlslparser](https://github.com/Thekla/hlslparser) © Unknown Worlds Entertainment; [GLM](https://github.com/g-truc/glm) © G-Truc Creation; [SOIL2](https://github.com/SpartanJ/SOIL2) with stb_image (Sean Barrett) and `etc1_utils.c` © Google | preset expressions, shader translation, maths, image loading | MIT; MIT; MIT (or the Happy Bunny License); public domain / MIT, `etc1_utils.c` Apache 2.0 |

Fernplay reads classic skins (`.wsz`) that the user supplies; it ships no skin artwork of others. Likewise it ships no MilkDrop presets: the visualizer uses a folder the user picks.

**projectM and the LGPL.** `projectM-4.dll` and `projectM-4-playlist.dll` are libprojectM 4.1.8, unmodified, built from the release's source with `tools/Build-ProjectM.ps1` (download addresses, SHA-256 sums and build settings are in that script and in `lib/projectM/README.md`); Fernplay only calls their C API. You may replace these DLLs with your own build of libprojectM 4.1.x with the same API. The source is at https://github.com/projectM-visualizer/projectm/releases/tag/v4.1.8. The full licence texts ship in the `licenses` folder next to Fernplay.exe: `projectM-LGPL-2.1.txt`, `GLEW-LICENSE.txt`, `projectm-eval-LICENSE.md`, `hlslparser-LICENSE.txt` and `SOIL2-etc1_utils-Apache-2.0.txt`.

Audio decoding itself is done by Windows (Media Foundation and its MP3 codec), which is part of the operating system and not shipped with Fernplay.

## NAudio: MIT License

Copyright (c) Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## System.Security.Cryptography.ProtectedData: MIT License

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Webamp: MIT License

Copyright (c) [2015] [Jordan Eldredge]

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
