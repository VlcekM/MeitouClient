# Builds the Windows release of the game (src/Meitou.Game, meitou.exe) and the world viewer (meitou-viewer.exe) into a zip that runs as is: self-contained .NET, NVIDIA's DLSS
# runtime and Streamline plugins and AMD's FidelityFX library next to the exe, their licences in licenses\. The NVIDIA and AMD files
# come from their official GitHub releases (cached in -Cache); none of them is in the repository.
#   pwsh -File tools/scripts/package-release.ps1 -Version 0.1.0 [-Output out] [-Cache C:\Temp\release-cache]
# (PowerShell 7: Windows PowerShell 5.1 writes the zip with backslashes in its paths, which some unzippers reject.)
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $Output = "out",
    [string] $Cache = (Join-Path ([IO.Path]::GetTempPath()) "meitou-release-cache")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$name = "Meitou-$Version-win-x64"
$stage = Join-Path $Output $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, $Cache | Out-Null

# The game (meitou.exe) and the world viewer (meitou-viewer.exe), self-contained (no .NET install needed) and not single-file: the
# runtime and our Meitou.*.dll lie beside the exes, shared by both (same build, same runtime), so code mods can reference and load
# them. A numeric version goes into the assembly version too.
$numeric = if ($Version -match '^\d+(\.\d+){1,3}') { $Matches[0] } else { "0.0.0" }
foreach ($project in @("src\Meitou.Game\Meitou.Game.csproj", "tools\Meitou.ModelViewer\Meitou.ModelViewer.csproj")) {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true `
        -p:DebugType=embedded -p:Version=$numeric -p:InformationalVersion=$Version -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}
# The viewer loads Streamline only when started with DLSS chosen (the game keeps the choice in meitou.user.json instead).
Set-Content -Path (Join-Path $stage "meitou-viewer-dlss.bat") -Encoding ASCII -Value "@`"%~dp0meitou-viewer.exe`" --world --town `"The Hub`" --upscaler dlss %*`r`n@if errorlevel 1 pause"

function Get-Archive([string] $url) {
    $file = Join-Path $Cache ([IO.Path]::GetFileName($url))
    if (-not (Test-Path $file)) {
        Write-Host "download  $url"
        $ProgressPreference = "SilentlyContinue"
        Invoke-WebRequest -Uri $url -OutFile "$file.part"
        Move-Item "$file.part" $file
    }
    return $file
}

# Copies the archive's entries at the given paths (forward slashes, as stored) to $dir under their file names; every one must exist.
function Copy-Entries([string] $zip, [string[]] $paths, [string] $dir, [string] $prefix = "") {
    New-Item -ItemType Directory -Force $dir | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($p in $paths) {
            $entry = $archive.Entries | Where-Object { $_.FullName -eq $p } | Select-Object -First 1
            if ($null -eq $entry) { throw "$p not found in $zip" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dir ($prefix + $entry.Name)), $true)
        }
    } finally { $archive.Dispose() }
}

# NVIDIA Streamline 2.14.1 (the headers src/Meitou.Rendering/Upscalers/Streamline.cs follows): the production (signed) plugins of bin/x64,
# not bin/x64/development, and the DLSS runtime. Loaded next to the exe (Streamline.cs).
$sl = Get-Archive "https://github.com/NVIDIA-RTX/Streamline/releases/download/v2.14.1/streamline-sdk-v2.14.1.zip"
Copy-Entries $sl @("bin/x64/sl.interposer.dll", "bin/x64/sl.common.dll", "bin/x64/sl.dlss.dll", "bin/x64/nvngx_dlss.dll", "bin/x64/NvLowLatencyVk.dll") $stage
Copy-Entries $sl @("license.txt", "3rd-party-licenses.md", "bin/x64/nvngx_dlss.license.txt", "bin/x64/reflex.license.txt") (Join-Path $stage "licenses\NVIDIA") "streamline-"

# AMD FidelityFX SDK 1.1.4 (the FidelityFX API headers FsrUpscaler.cs follows): its signed prebuilt Vulkan library.
$ffx = Get-Archive "https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/download/v1.1.4/FidelityFX-SDK-v1.1.4.zip"
Copy-Entries $ffx @("PrebuiltSignedDLL/amd_fidelityfx_vk.dll") $stage
Copy-Entries $ffx @("sdk/LICENSE.txt") (Join-Path $stage "licenses\AMD") "fidelityfx-"

# Our licence, the GPL exception for the NVIDIA libraries, the notices, and how to start.
Copy-Item (Join-Path $root "LICENSE"), (Join-Path $root "LICENSE-EXCEPTION.md"), (Join-Path $root "THIRD_PARTY_NOTICES.md") $stage
$readme = @"
Meitou $Version (Windows x64)
==============================

A reimplementation of the Kenshi engine. It reads your own Kenshi install; no game files are included.

Two programs
------------
meitou.exe          The game: the world with the Kenshi camera.
meitou-viewer.exe   The world viewer: a free camera over the world, with the Faithful / Meitou switches (F1-F8), frame statistics
                    (F11), the profiler (F12) and every renderer option (meitou-viewer.exe --world --help lists them).
                    meitou-viewer-dlss.bat starts it at The Hub with DLSS.

Start
-----
1. Unzip anywhere.
2. Run meitou.exe or meitou-viewer.exe (both start at The Hub).
   - A Steam install of Kenshi is found by itself.
   - Otherwise the console asks for your Kenshi folder (the one with kenshi_x64.exe and the data folder) once and keeps it in
     meitou.local.json next to the exe. You can also edit that file, or set the KENSHI_PATH environment variable.
3. Tab opens the settings (draw distances, anti-aliasing: FXAA, TAA, FSR or DLSS). F10 lists the keys.
   DLSS (NVIDIA RTX cards): in the game, pick it on the Tab panel's anti-aliasing slider and restart once (the choice is kept in
   meitou.user.json); in the viewer, start meitou-viewer-dlss.bat (or add --upscaler dlss).

Requirements: Windows 10/11 x64, a Vulkan 1.3 GPU with a current driver.

Licences
--------
Meitou is GPL-3.0-or-later (LICENSE) with an additional permission for the NVIDIA DLSS runtime (LICENSE-EXCEPTION.md).
The NVIDIA files (nvngx_dlss.dll, sl.*.dll, NvLowLatencyVk.dll) are NVIDIA's, under the licences in licenses\NVIDIA, not the GPL.
amd_fidelityfx_vk.dll is AMD's, under the MIT licence in licenses\AMD. Other components: THIRD_PARTY_NOTICES.md.
"@
Set-Content -Path (Join-Path $stage "README.txt") -Value $readme -Encoding UTF8

$zipPath = Join-Path $Output "$name.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $stage), (Join-Path (Resolve-Path $Output) "$name.zip"), [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host "release   $zipPath"
