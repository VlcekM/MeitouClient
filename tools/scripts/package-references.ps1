# Builds the reference pack code mods compile against (docs/modding.md "Reference pack"): Meitou-<version>-refs.zip, one top folder holding, for
# net11.0 (standard builds) and net10.0 (legacy macOS builds), ref/: the compiler's reference assemblies of MeitouClient, Meitou.Simulation,
# Meitou.Core and Meitou.Data beside the game's own builds of its other managed libraries (0Harmony, Bepu, DotRecast, Silk.NET and the rest,
# which the compiler needs for any member typed in them), and publicized/: the same four with every type and member public and bodies
# stripped (tools/Meitou.ReferencePack), whose libraries the targets take from ref/; plus Meitou.Mod.targets, which a mod imports, the access
# attribute file a publicized mod compiles in, and the licences. The Meitou assemblies carry the release's version, as package-release.ps1
# stamps the game's, so a mod built against the pack binds to that release.
# Nothing here touches the game's own build or zips: the four projects build separately under -Work.
#   pwsh -File tools/scripts/package-references.ps1 -Version 0.21.0 [-Output out] [-Work <scratch folder>] [-Frameworks net11.0,net10.0]
# (Runs on PowerShell 7 and Windows PowerShell 5.1: the zip is written entry by entry with forward slashes.)
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $Output = "out",
    [string] $Work = (Join-Path ([IO.Path]::GetTempPath()) "meitou-references"),
    [string[]] $Frameworks = @("net11.0", "net10.0")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$packFiles = Join-Path $root "tools/reference-pack"
$assemblies = @("MeitouClient", "Meitou.Simulation", "Meitou.Core", "Meitou.Data")
# As package-release.ps1: a numeric version goes into the assembly version, the full one into the informational version.
$numeric = if ($Version -match '^\d+(\.\d+){1,3}') { $Matches[0] } else { "0.0.0" }
$parsed = [Version]$numeric
$expected = New-Object Version $parsed.Major, $parsed.Minor, ([Math]::Max($parsed.Build, 0)), ([Math]::Max($parsed.Revision, 0))
$name = "Meitou-$Version-refs"
New-Item -ItemType Directory -Force $Output, $Work | Out-Null
$stage = Join-Path (Resolve-Path $Output).Path $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

# Builds the game project (which builds the other three) for one framework into its own artifacts folder, apart from the normal bin/ and obj/;
# returns the artifacts folder. No debug files: nothing in the pack names the builder's folders.
function Build-Framework([string] $framework) {
    # net10.0 is the legacy macOS build (Directory.Build.props): the same sources on .NET 10.
    $legacy = if ($framework -eq "net10.0") { "true" } elseif ($framework -eq "net11.0") { "false" } else { throw "unknown framework $framework" }
    $artifacts = Join-Path $Work $framework
    if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
    dotnet build (Join-Path $root "src/Meitou.Game/Meitou.Game.csproj") -c Release --artifacts-path $artifacts -p:MeitouLegacyMac=$legacy `
        -p:ProduceReferenceAssembly=true -p:DebugType=none -p:Version=$numeric -p:InformationalVersion=$Version -p:BuildGitTag=v$Version | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build for $framework failed" }
    return $artifacts
}

# The game's build output folder (MeitouClient.dll and every managed library beside it).
function Get-GameBin([string] $artifacts) {
    $bin = Get-ChildItem (Join-Path $artifacts "bin/Meitou.Game") -Directory | Select-Object -First 1
    if ($null -eq $bin) { throw "no build output under $artifacts" }
    return $bin.FullName
}

# ref/: the compiler's own reference assemblies (obj/<project>/<pivot>/ref/), then the game's other managed libraries as built.
function Write-ReferenceSet([string] $artifacts, [string] $bin, [string] $dir) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($assembly in $assemblies) {
        $ref = Get-ChildItem (Join-Path $artifacts "obj") -Recurse -Filter "$assembly.dll" |
            Where-Object { $_.Directory.Name -eq "ref" } |
            Select-Object -First 1
        if ($null -eq $ref) { throw "no reference assembly for $assembly" }
        Copy-Item $ref.FullName $dir
    }
    Copy-Libraries $bin $dir
}

# The managed libraries beside the game that are not Meitou's own (0Harmony, Bepu, DotRecast, Silk.NET, ...): the files the game zip ships,
# under the same licences (THIRD_PARTY_NOTICES.md). Native libraries live under runtimes/ and are not copied.
function Copy-Libraries([string] $bin, [string] $dir) {
    $ours = $assemblies | ForEach-Object { "$_.dll" }
    foreach ($file in Get-ChildItem $bin -Filter *.dll -File) {
        if ($ours -contains $file.Name) { continue }
        Copy-Item $file.FullName $dir
    }
}

# publicized/: the built Meitou assemblies run through the publicizer; Meitou.Mod.targets takes the libraries for this set from ref/.
function Write-PublicizedSet([string] $bin, [string] $dir) {
    $built = $assemblies | ForEach-Object { Join-Path $bin "$_.dll" }
    dotnet run --project (Join-Path $root "tools/Meitou.ReferencePack/Meitou.ReferencePack.csproj") -c Release -- $dir @built | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "meitou-reference-pack failed for $dir" }
}

# Every Meitou assembly in a set must carry the release's version, or a mod built against it would not bind to the release.
function Test-Versions([string] $dir) {
    foreach ($assembly in $assemblies) {
        $identity = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $dir "$assembly.dll"))
        if ($identity.Version -ne $expected) { throw "$dir/$assembly.dll is version $($identity.Version), not $expected" }
    }
}

foreach ($framework in $Frameworks) {
    $artifacts = Build-Framework $framework
    $bin = Get-GameBin $artifacts
    $refDir = Join-Path $stage "$framework/ref"
    Write-ReferenceSet $artifacts $bin $refDir
    Test-Versions $refDir
    $pubDir = Join-Path $stage "$framework/publicized"
    Write-PublicizedSet $bin $pubDir
    Test-Versions $pubDir
}

# The targets file, Meitou's licence, the notices covering every library in the pack, and the Apache-2.0 text Bepu's licence asks to accompany it.
Copy-Item (Join-Path $packFiles "Meitou.Mod.targets"), (Join-Path $packFiles "Meitou.IgnoresAccessChecksTo.cs") $stage
Copy-Item (Join-Path $root "LICENSE"), (Join-Path $root "THIRD_PARTY_NOTICES.md") $stage
Copy-Item (Join-Path $packFiles "licenses") $stage -Recurse
$title = "Meitou $Version reference pack"
$readme = @"
$title
$('=' * $title.Length)

Compile code mods for Meitou $Version without a game install. The game supplies the real assemblies when it loads a mod and never loads
these copies: the four Meitou assemblies here are compile-only reference assemblies that .NET refuses to run, and the other DLLs are the
game's own builds of its libraries, which the game always takes from its own folder. The guide: docs/modding.md "Reference pack" in
https://github.com/VlcekM/MeitouClient.

net11.0/   standard builds (Windows, Linux, macOS)
net10.0/   legacy macOS builds
  ref/          MeitouClient.dll, Meitou.Simulation.dll, Meitou.Core.dll, Meitou.Data.dll (the public API), 0Harmony.dll and the game's other
                libraries (Bepu, DotRecast, Silk.NET, ...), needed for any member typed in them
  publicized/   the four Meitou assemblies with every type and member public, for mods that reach internals (the libraries come from ref/)

Use
---
In the mod's .csproj:
    <Import Project="path/to/$name/Meitou.Mod.targets" />
For the publicized set, also set <MeitouPublicized>true</MeitouPublicized> anywhere in the project: the mod then compiles in
Meitou.IgnoresAccessChecksTo.cs, which lets it reach those members at run time. Internals change between releases without notice.
A mod built against a nightly's pack carries the nightly's version and may not load on the previous full release.

Licences
--------
The Meitou assemblies are built from MeitouClient's source (GPL-3.0-or-later, LICENSE). The other libraries are under their own licences,
listed in THIRD_PARTY_NOTICES.md; licenses/Apache-2.0.txt is the text for BepuPhysics and BepuUtilities.
"@
Set-Content -Path (Join-Path $stage "README.txt") -Value $readme -Encoding UTF8

# The zip: one top folder, entries with forward slashes on every PowerShell.
$zipPath = Join-Path (Resolve-Path $Output).Path "$name.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $entry = "$name/" + $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
Write-Host "references $zipPath"
