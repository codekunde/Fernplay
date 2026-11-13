<#
.SYNOPSIS
Builds the visualizer's native DLLs (libprojectM 4.1.8 + GLEW 2.2.0) into lib\projectM\win-x64.

.DESCRIPTION
Only needed to update or re-check the DLLs; they are committed, so building Fernplay needs no C++ tools.
Needs Visual Studio 2019 Build Tools (or newer) with the C++ workload and a Windows 10 SDK.
Every download is pinned by SHA-256. projectM is built unmodified (LGPL-2.1):
- shared GLEW: projectM never calls glewInit, Fernplay does, so both must use the same glew32.dll;
- static C runtime (/MT): no Visual C++ redistributable needed;
- /FIiterator: TextureManager.cpp uses std::back_inserter without <iterator>, which MSVC 14.29 does not pull in.

.PARAMETER Work
Scratch folder for downloads and the build (default: a new folder under %TEMP%).
#>
param([string]$Work = (Join-Path ([IO.Path]::GetTempPath()) "fernplay-projectm-$(Get-Random)"))

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo 'lib\projectM\win-x64'
$licenses = Join-Path $repo 'lib\projectM\licenses'

$downloads = @(
    @{ Name = 'projectm'; Url = 'https://github.com/projectM-visualizer/projectm/releases/download/v4.1.8/libprojectM-4.1.8.tar.gz'
       File = 'libprojectM-4.1.8.tar.gz'; Sha256 = 'F72135E8B8E5FD807F908537255B41AC7BA4830309265E830CB756535487239E' }
    @{ Name = 'glew'; Url = 'https://github.com/nigels-com/glew/releases/download/glew-2.2.0/glew-2.2.0-win32.zip'
       File = 'glew-2.2.0-win32.zip'; Sha256 = 'EA6B14A1C6C968D0034E61FF6CB242CFF2CE0EDE79267A0F2B47B1B0B652C164' }
    # SOIL2's etc1_utils.c, compiled into projectM-4.dll, is Apache-2.0.
    @{ Name = 'apache'; Url = 'https://www.apache.org/licenses/LICENSE-2.0.txt'
       File = 'Apache-2.0.txt'; Sha256 = 'CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30' }
    # projectM needs CMake 3.21+; VS 2019's bundled one is 3.20.
    @{ Name = 'cmake'; Url = 'https://github.com/Kitware/CMake/releases/download/v3.31.6/cmake-3.31.6-windows-x86_64.zip'
       File = 'cmake-3.31.6-windows-x86_64.zip'; Sha256 = 'D163CD3AB4959B0A53FA8988F2DDBD2E6C501658201E6A154386BAD9DBE4F836' }
)

New-Item -ItemType Directory -Force $Work | Out-Null
foreach ($d in $downloads) {
    $file = Join-Path $Work $d.File
    if (-not (Test-Path $file)) { Invoke-WebRequest $d.Url -OutFile $file }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($hash -ne $d.Sha256) { throw "$($d.File): SHA-256 $hash, expected $($d.Sha256)" }
    if ($file.EndsWith('.txt')) { continue }
    # Each archive in its own empty folder.
    $dir = Join-Path $Work $d.Name
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory $dir | Out-Null
    if ($file.EndsWith('.tar.gz')) { tar -xzf $file -C $dir; if ($LASTEXITCODE) { throw "tar failed on $file" } }
    else { Expand-Archive $file $dir }
}

$src = Join-Path $Work 'projectm\libprojectM-4.1.8'
$glew = Join-Path $Work 'glew\glew-2.2.0'
$cmake = Join-Path $Work 'cmake\cmake-3.31.6-windows-x86_64\bin\cmake.exe'
$build = Join-Path $Work 'build'
$install = Join-Path $Work 'install'

function Invoke-CMake {
    & $cmake @args
    if ($LASTEXITCODE -ne 0) { throw "cmake $($args -join ' ') failed ($LASTEXITCODE)" }
}

Invoke-CMake -S $src -B $build -G 'Visual Studio 16 2019' -A x64 `
    '-DGLEW_USE_STATIC_LIBS=OFF' "-DGLEW_ROOT=$glew" "-DCMAKE_PREFIX_PATH=$glew" `
    '-DENABLE_PLAYLIST=ON' '-DBUILD_TESTING=OFF' "-DCMAKE_INSTALL_PREFIX=$install" `
    '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded' `
    '-DCMAKE_CXX_FLAGS=/DWIN32 /D_WINDOWS /EHsc /FIiterator' '-DCMAKE_C_FLAGS=/DWIN32 /D_WINDOWS'
Invoke-CMake --build $build --config Release --parallel
Invoke-CMake --install $build --config Release

New-Item -ItemType Directory -Force $out, $licenses | Out-Null
Copy-Item (Join-Path $install 'bin\projectM-4.dll'), (Join-Path $install 'bin\projectM-4-playlist.dll') $out -Force
Copy-Item (Join-Path $glew 'bin\Release\x64\glew32.dll') $out -Force
Copy-Item (Join-Path $src 'LICENSE.txt') (Join-Path $licenses 'projectM-LGPL-2.1.txt') -Force
Copy-Item (Join-Path $glew 'LICENSE.txt') (Join-Path $licenses 'GLEW-LICENSE.txt') -Force
Copy-Item (Join-Path $src 'vendor\projectm-eval\LICENSE.md') (Join-Path $licenses 'projectm-eval-LICENSE.md') -Force
Copy-Item (Join-Path $src 'vendor\hlslparser\LICENSE') (Join-Path $licenses 'hlslparser-LICENSE.txt') -Force
Copy-Item (Join-Path $Work 'Apache-2.0.txt') (Join-Path $licenses 'SOIL2-etc1_utils-Apache-2.0.txt') -Force

Get-ChildItem $out | ForEach-Object { '{0,-26} {1,10:N0}  {2}' -f $_.Name, $_.Length, (Get-FileHash $_.FullName).Hash }
"Work folder (delete when done): $Work"
