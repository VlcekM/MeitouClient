# Builds a release of the game (src/Meitou.Game) and the world viewer (meitou-viewer) for one runtime into a zip that runs as is:
# self-contained .NET under the single top folder Meitou-<version>-<rid>. The Windows zip also carries NVIDIA's DLSS runtime and Streamline
# plugins, AMD's FidelityFX library and NVIDIA's NGX client library (meitou_ngx.dll, for DLSS with frame generation) next to the exes, and the Linux zip
# NVIDIA's DLSS model and NGX client library and our build of AMD's FidelityFX library, their licences in licenses/; they come from NVIDIA's and AMD's
# official GitHub repositories (cached in -Cache; the FidelityFX source is built by build-ffx-linux.sh) and none of them is in the repository. Linux and macOS zips are made on a Unix machine with zip(1), which stores the execute
# bits: the Linux zip on Linux (build-ngx-shim.sh links with GNU ld), macOS zips on a Mac, which ad-hoc signs the binaries (Apple Silicon
# refuses unsigned code).
#   pwsh -File tools/scripts/package-release.ps1 -Version 0.1.0 [-Rid win-x64|linux-x64|osx-x64|osx-arm64] [-Output out] [-Cache C:\Temp\release-cache]
# (PowerShell 7: Windows PowerShell 5.1 writes the zip with backslashes in its paths, which some unzippers reject.)
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [ValidateSet("win-x64", "linux-x64", "osx-x64", "osx-arm64")] [string] $Rid = "win-x64",
    [string] $Output = "out",
    [string] $Cache = (Join-Path ([IO.Path]::GetTempPath()) "meitou-release-cache")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$windows = $Rid -like "win-*"
$mac = $Rid -like "osx-*"
$linux = $Rid -like "linux-*"
if (-not $windows -and $IsWindows) { throw "make the $Rid zip on Linux or macOS: a zip made on Windows has no execute bits" }
if ($mac -and -not $IsMacOS) { throw "make the $Rid zip on macOS: its binaries need an ad-hoc signature" }
if ($linux -and -not $IsLinux) { throw "make the $Rid zip on Linux: build-ngx-shim.sh links an ELF library with GNU ld" }
$exe = if ($windows) { ".exe" } else { "" }
# The game executable's name: Meitou.Game's AssemblyName.
$game = ([xml](Get-Content (Join-Path $root "src/Meitou.Game/Meitou.Game.csproj"))).Project.PropertyGroup.AssemblyName | Where-Object { $_ } | Select-Object -First 1
if (-not $game) { throw "no AssemblyName in Meitou.Game.csproj" }
$name = "Meitou-$Version-$Rid"
$stage = Join-Path $Output $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, $Cache | Out-Null

# The game and the world viewer (meitou-viewer), self-contained (no .NET install needed) and not single-file: the runtime and our
# Meitou.*.dll lie beside the executables, shared by both (same build, same runtime), so code mods can reference and load them. A numeric
# version goes into the assembly version too.
$numeric = if ($Version -match '^\d+(\.\d+){1,3}') { $Matches[0] } else { "0.0.0" }
foreach ($project in @("src/Meitou.Game/Meitou.Game.csproj", "tools/Meitou.ModelViewer/Meitou.ModelViewer.csproj")) {
    dotnet publish (Join-Path $root $project) -c Release -r $Rid --self-contained true `
        -p:DebugType=embedded -p:Version=$numeric -p:InformationalVersion=$Version -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}
# The Unix runtime packs carry native debug symbols (*.dbg, *.dwarf, *.dSYM; ~250 MB): not shipped.
Get-ChildItem $stage -Recurse -Include *.dbg, *.dwarf, *.dSYM | Remove-Item -Recurse -Force

# The settings the launcher's Graphics page shows, under the names this build saves them (docs/launcher.md "Graphics"). The file is the same
# for every runtime, so the host's own build writes it: the staged exe may be for another one.
dotnet run --project (Join-Path $root "src/Meitou.Game/Meitou.Game.csproj") -c Release -- --settings-schema (Join-Path (Resolve-Path $stage) "settings-schema.json")
if ($LASTEXITCODE -ne 0) { throw "writing settings-schema.json failed" }

function Get-Archive([string] $url, [string] $name = [IO.Path]::GetFileName($url)) {
    $file = Join-Path $Cache $name
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

if ($windows) {
    # The viewer loads Streamline only when started with DLSS chosen (the game loads it on every startup).
    Set-Content -Path (Join-Path $stage "meitou-viewer-dlss.bat") -Encoding ASCII -Value "@`"%~dp0meitou-viewer.exe`" --world --town `"The Hub`" --upscaler dlss %*`r`n@if errorlevel 1 pause"

    # NVIDIA Streamline 2.14.16 (the headers src/Meitou.Rendering/Upscalers/Streamline.cs follows): the production (signed) plugins of bin/x64,
    # not bin/x64/development, and the DLSS runtime. Loaded next to the exe (Streamline.cs).
    $sl = Get-Archive "https://github.com/NVIDIA-RTX/Streamline/releases/download/v2.14.16/streamline-sdk-v2.14.16.zip"
    Copy-Entries $sl @("bin/x64/sl.interposer.dll", "bin/x64/sl.common.dll", "bin/x64/sl.dlss.dll", "bin/x64/nvngx_dlss.dll", "bin/x64/NvLowLatencyVk.dll") $stage
    Copy-Entries $sl @("license.txt", "3rd-party-licenses.md", "bin/x64/nvngx_dlss.license.txt", "bin/x64/reflex.license.txt") (Join-Path $stage "licenses/NVIDIA") "streamline-"

    # AMD FidelityFX SDK 1.1.4 (the FidelityFX API headers FsrUpscaler.cs follows): its signed prebuilt Vulkan library.
    $ffx = Get-Archive "https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/download/v1.1.4/FidelityFX-SDK-v1.1.4.zip"
    Copy-Entries $ffx @("PrebuiltSignedDLL/amd_fidelityfx_vk.dll") $stage
    Copy-Entries $ffx @("sdk/LICENSE.txt") (Join-Path $stage "licenses/AMD") "fidelityfx-"

    # meitou_ngx.dll, DLSS through NGX when frame generation is on (Ngx.cs): the DLSS SDK 310.9.16 static client library linked as is into a DLL
    # with MSVC's linker, as tools/scripts/build-ngx-shim-win.sh does with lld-link and xwin on Linux (same library, checksum and exports).
    $ngxLib = Get-Archive "https://github.com/NVIDIA/DLSS/raw/v310.9.16/lib/Windows_x86_64/x64/nvsdk_ngx_s.lib" "nvsdk_ngx_s-310.9.16.lib"
    if ((Get-FileHash $ngxLib -Algorithm SHA256).Hash -ne "7B9A5239C1145AF5F645AB472AA066F9EA79DB36AC819562312AAE69D65F3765") { throw "${ngxLib}: SHA-256 mismatch" }
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw "no Visual Studio with the C++ tools (MSVC's linker builds meitou_ngx.dll)" }
    $ngxExports = (@("Init_with_ProjectID", "Shutdown1", "GetCapabilityParameters", "AllocateParameters", "DestroyParameters", "CreateFeature1", "ReleaseFeature",
        "EvaluateFeature_C", "RequiredExtensions" | ForEach-Object { "-export:NVSDK_NGX_VULKAN_$_" }) +
        @("SetI", "SetUI", "SetF", "SetVoidPointer", "GetI", "GetUI" | ForEach-Object { "-export:NVSDK_NGX_Parameter_$_" })) -join " "
    $ngxDir = Join-Path $Cache "ngx-link"
    New-Item -ItemType Directory -Force $ngxDir | Out-Null
    cmd /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && link -dll -nologo -out:`"$ngxDir\meitou_ngx.dll`" -machine:x64 -defaultlib:libcmt -defaultlib:libucrt -defaultlib:libvcruntime -defaultlib:libcpmt -defaultlib:kernel32 -defaultlib:advapi32 -defaultlib:user32 -wholearchive:`"$ngxLib`" $ngxExports"
    if ($LASTEXITCODE -ne 0) { throw "linking meitou_ngx.dll failed" }
    Copy-Item (Join-Path $ngxDir "meitou_ngx.dll") $stage
    New-Item -ItemType Directory -Force (Join-Path $stage "licenses/NVIDIA") | Out-Null
    Copy-Item (Get-Archive "https://github.com/NVIDIA/DLSS/raw/v310.9.16/LICENSE.txt" "dlss-310.9.16-LICENSE.txt") (Join-Path $stage "licenses/NVIDIA/dlss-LICENSE.txt")
}

if ($linux) {
    # NVIDIA DLSS SDK 310.9.16: its production (rel) DLSS model and its NGX client library (libmeitou_ngx.so, built from the SDK's static
    # library by build-ngx-shim.sh), both loaded from next to the executable (Ngx.cs).
    $dlss = "https://github.com/NVIDIA/DLSS/raw/v310.9.16"
    Copy-Item (Get-Archive "$dlss/lib/Linux_x86_64/rel/libnvidia-ngx-dlss.so.310.9.16") $stage
    bash (Join-Path $root "tools/scripts/build-ngx-shim.sh") $stage $Cache
    if ($LASTEXITCODE -ne 0) { throw "build-ngx-shim.sh failed" }
    New-Item -ItemType Directory -Force (Join-Path $stage "licenses/NVIDIA") | Out-Null
    Copy-Item (Get-Archive "$dlss/LICENSE.txt" "dlss-310.9.16-LICENSE.txt") (Join-Path $stage "licenses/NVIDIA/dlss-LICENSE.txt")

    # FSR 3 upscaling and frame generation: our unofficial Linux build of AMD FidelityFX SDK 1.1.4 (build-ffx-linux.sh: AMD's source at the
    # pinned commit plus tools/patches), loaded from next to the executable (FsrUpscaler.cs). AMD's licence goes with the others.
    bash (Join-Path $root "tools/scripts/build-ffx-linux.sh") $stage
    if ($LASTEXITCODE -ne 0) { throw "build-ffx-linux.sh failed" }
    New-Item -ItemType Directory -Force (Join-Path $stage "licenses/AMD") | Out-Null
    Move-Item -Force (Join-Path $stage "LICENSE-FidelityFX.txt") (Join-Path $stage "licenses/AMD/fidelityfx-LICENSE.txt")
}

# Our licence, the GPL exception for the NVIDIA libraries, the notices, and how to start.
Copy-Item (Join-Path $root "LICENSE"), (Join-Path $root "LICENSE-EXCEPTION.md"), (Join-Path $root "THIRD_PARTY_NOTICES.md") $stage
$platform = @{ "win-x64" = "Windows x64"; "linux-x64" = "Linux x64"; "osx-x64" = "macOS Intel"; "osx-arm64" = "macOS Apple Silicon" }[$Rid]
$title = "Meitou $Version ($platform)"
$requirements = if ($mac) {
    "Requirements: macOS with a Vulkan loader and MoltenVK installed (they are not included), e.g. from the LunarG Vulkan SDK.`n" +
    "The macOS build is experimental and untested: MoltenVK may not offer everything the renderer needs (Vulkan 1.3).`n" +
    "If macOS blocks the program after a browser download, remove the quarantine flag: xattr -dr com.apple.quarantine <this folder>."
} else {
    "Requirements: Linux x64 with a Vulkan 1.3 GPU driver and the Vulkan loader (libvulkan.so.1, e.g. the libvulkan1 package), an X11 or`n" +
    "Wayland session, and PipeWire, PulseAudio or ALSA for sound. OpenAL Soft, GLFW and shaderc are included."
}
if ($windows) {
    $readme = @"
$title
$('=' * $title.Length)

A reimplementation of the Kenshi engine. It reads your own Kenshi install; no game files are included.

Two programs
------------
$("$game.exe".PadRight(20))The game: the world with the Kenshi camera.
meitou-viewer.exe   The world viewer: a free camera over the world, with the Faithful / Meitou switches (F1-F8), frame statistics
                    (F11), the profiler (F12) and every renderer option (meitou-viewer.exe --world --help lists them).
                    meitou-viewer-dlss.bat starts it at The Hub with DLSS.

Start
-----
1. Unzip anywhere.
2. Run $game.exe or meitou-viewer.exe (both start at The Hub).
   - A Steam install of Kenshi is found by itself.
   - Otherwise the console asks for your Kenshi folder (the one with kenshi_x64.exe and the data folder) once and keeps it in
     meitou.local.json next to the exe. You can also edit that file, or set the KENSHI_PATH environment variable.
3. Tab opens the settings (draw distances, anti-aliasing/upscaling: FXAA, TAA, FSR or DLSS). F10 lists the keys.
   DLSS (NVIDIA RTX cards): in the game, pick it on the Tab panel's anti-aliasing slider, with no restart needed (the choice is kept in
   meitou.user.json); in the viewer, start meitou-viewer-dlss.bat (or add --upscaler dlss).

Requirements: Windows 10/11 x64, a Vulkan 1.3 GPU with a current driver.
Meitou's files are not code-signed yet: if Windows 11's Smart App Control blocks one, the game says so and closes. Turn Smart App Control
off in Windows Security > App & browser control to play (on some Windows versions it cannot be turned back on without reinstalling).

Licences
--------
Meitou is GPL-3.0-or-later (LICENSE) with an additional permission for the NVIDIA DLSS runtime (LICENSE-EXCEPTION.md).
The NVIDIA files (nvngx_dlss.dll, sl.*.dll, NvLowLatencyVk.dll, meitou_ngx.dll) are NVIDIA's, under the licences in licenses\NVIDIA, not the GPL.
amd_fidelityfx_vk.dll is AMD's, under the MIT licence in licenses\AMD. Other components: THIRD_PARTY_NOTICES.md.
"@
} else {
    $antiAliasing = if ($linux) { "FXAA, TAA, FSR or DLSS" } else { "FXAA or TAA" }
    $dlssNote = if ($linux) {
        "`n   DLSS needs an NVIDIA RTX card on NVIDIA's own driver (not Nouveau or NVK), glibc 2.35 or newer, and libffi, which desktop systems have." +
        "`n   If the game closes at startup on an NVIDIA card, start it with MEITOU_DLSS=0 (MEITOU_DLSS=0 ./$game): it then runs without DLSS."
    } else { "" }
    $nvidiaLicence = if ($linux) {
        "`nThe GPL has an additional permission for the NVIDIA DLSS runtime (LICENSE-EXCEPTION.md): libnvidia-ngx-dlss.so and libmeitou_ngx.so are NVIDIA's,`n" +
        "under the licence in licenses/NVIDIA, not the GPL.`n" +
        "libamd_fidelityfx_vk.so is an unofficial Linux build of AMD's FidelityFX SDK (not made or supported by AMD), under the MIT licence in licenses/AMD."
    } else { "" }
    $readme = @"
$title
$('=' * $title.Length)

A reimplementation of the Kenshi engine. It reads your own Kenshi install (the Windows game's files, e.g. from Steam with Proton);
no game files are included.

Two programs
------------
$($game.PadRight(16))The game: the world with the Kenshi camera.
meitou-viewer   The world viewer: a free camera over the world, with the Faithful / Meitou switches (F1-F8), frame statistics (F11),
                the profiler (F12) and every renderer option (./meitou-viewer --world --help lists them).

Start
-----
1. Unzip anywhere (the Meitou launcher does this for you).
2. Run ./$game or ./meitou-viewer from a terminal (both start at The Hub).
   - Give it your Kenshi folder (the one with kenshi_x64.exe and the data folder): the KENSHI_PATH environment variable, or the
     console asks once and keeps it in meitou.local.json next to the program.
3. Tab opens the settings (draw distances, anti-aliasing: $antiAliasing). F10 lists the keys.$dlssNote

$requirements

Licences
--------
Meitou is GPL-3.0-or-later (LICENSE). Other components: THIRD_PARTY_NOTICES.md.$nvidiaLicence
"@
}
Set-Content -Path (Join-Path $stage "README.txt") -Value $readme -Encoding UTF8

$zipPath = Join-Path $Output "$name.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
if (-not (Test-Path (Join-Path $stage "settings-schema.json"))) { throw "settings-schema.json missing from $stage" }
foreach ($program in $game, "meitou-viewer") {
    if (-not (Test-Path (Join-Path $stage "$program$exe"))) { throw "$program$exe missing from $stage" }
}
if ($windows) {
    [IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $stage), (Join-Path (Resolve-Path $Output) "$name.zip"), [IO.Compression.CompressionLevel]::Optimal, $true)
} else {
    $programs = @($game, "meitou-viewer", "createdump") | ForEach-Object { Join-Path $stage $_ } | Where-Object { Test-Path $_ }
    chmod +x @programs
    if ($LASTEXITCODE -ne 0) { throw "chmod failed" }
    if ($mac) {
        # Ad-hoc signatures (no Apple identity): enough for Apple Silicon to run the code; Gatekeeper still flags browser downloads.
        $code = @($programs) + @(Get-ChildItem $stage -Filter *.dylib | ForEach-Object FullName)
        foreach ($file in $code) {
            codesign --force --sign - $file
            if ($LASTEXITCODE -ne 0) { throw "codesign $file failed" }
        }
    }
    # zip(1) keeps the Unix modes; one top folder, as the launcher expects (docs/launcher.md).
    Push-Location $Output
    try {
        zip -qr -X "$name.zip" $name
        if ($LASTEXITCODE -ne 0) { throw "zip failed" }
    } finally { Pop-Location }
}
Write-Host "release   $zipPath"
