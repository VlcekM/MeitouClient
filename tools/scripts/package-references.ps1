# Builds Meitou.References, the NuGet package code mods compile against (docs/modding.md "Reference package"): Meitou.References.<version>.nupkg.
# Under assemblies/ it holds, for net11.0 (standard builds) and net10.0 (legacy macOS builds), ref/: the compiler's reference assemblies of
# MeitouClient, Meitou.Simulation, Meitou.Core and Meitou.Data beside the game's own builds of its other managed libraries (0Harmony, Bepu,
# DotRecast, Silk.NET and the rest, which the compiler needs for any member typed in them), and publicized/: the same four with every type and
# member public and bodies stripped (tools/Meitou.ReferencePack), whose libraries the targets take from ref/. build/ holds
# Meitou.References.targets, which NuGet imports into the mod's project, and the access attribute file a publicized mod compiles in; beside
# them lie the readme, the icon and the licences. The Meitou assemblies carry the release's version, as package-release.ps1 stamps the
# game's, so a mod built against the package binds to that release.
# Nothing here touches the game's own build or zips: the four projects build separately under -Work, where the package is staged too.
#   pwsh -File tools/scripts/package-references.ps1 -Version 0.21.0 [-Output out] [-Work <scratch folder>] [-Frameworks net11.0,net10.0]
# (Runs on PowerShell 7 and Windows PowerShell 5.1.)
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $Output = "out",
    [string] $Work = (Join-Path ([IO.Path]::GetTempPath()) "meitou-references"),
    [string[]] $Frameworks = @("net11.0", "net10.0")
)
$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$packFiles = Join-Path $root "tools/reference-pack"
$assemblies = @("MeitouClient", "Meitou.Simulation", "Meitou.Core", "Meitou.Data")
# As package-release.ps1: a numeric version goes into the assembly version, the full one into the informational version.
$numeric = if ($Version -match '^\d+(\.\d+){1,3}') { $Matches[0] } else { "0.0.0" }
$parsed = [Version]$numeric
$expected = New-Object Version $parsed.Major, $parsed.Minor, ([Math]::Max($parsed.Build, 0)), ([Math]::Max($parsed.Revision, 0))
New-Item -ItemType Directory -Force $Output, $Work | Out-Null
# The package's files, laid out as in the package (tools/reference-pack/Meitou.References.csproj packs the folder as it is).
$stage = Join-Path (Resolve-Path $Work).Path "package"
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

# publicized/: the built Meitou assemblies run through the publicizer; Meitou.References.targets takes the libraries for this set from ref/.
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
    $refDir = Join-Path $stage "assemblies/$framework/ref"
    Write-ReferenceSet $artifacts $bin $refDir
    Test-Versions $refDir
    $pubDir = Join-Path $stage "assemblies/$framework/publicized"
    Write-PublicizedSet $bin $pubDir
    Test-Versions $pubDir
}

# build/: the targets file NuGet imports and the access attribute file. Beside them the readme and icon the package page shows, Meitou's
# licence, the notices covering every library in the package, and the Apache-2.0 text Bepu's licence asks to accompany it.
$build = Join-Path $stage "build"
New-Item -ItemType Directory -Force $build | Out-Null
Copy-Item (Join-Path $packFiles "Meitou.References.targets"), (Join-Path $packFiles "Meitou.IgnoresAccessChecksTo.cs") $build
Copy-Item (Join-Path $packFiles "README.md"), (Join-Path $root "THIRD_PARTY_NOTICES.md") $stage
Copy-Item (Join-Path $root "LICENSE") (Join-Path $stage "LICENSE.txt")
Copy-Item (Join-Path $root "assets/branding/meitou-128.png") (Join-Path $stage "icon.png")
Copy-Item (Join-Path $packFiles "licenses") $stage -Recurse

# The package: its version is the release's, in full (NuGet writes it normalized into the file name).
$packWork = Join-Path $Work "pack"
if (Test-Path $packWork) { Remove-Item $packWork -Recurse -Force }
$out = (Resolve-Path $Output).Path
Remove-Item (Join-Path $out "Meitou.References.*.nupkg") -Force -ErrorAction SilentlyContinue
dotnet pack (Join-Path $packFiles "Meitou.References.csproj") -c Release --artifacts-path $packWork -o $out `
    -p:MeitouReferencesStage=$stage -p:PackageVersion=$Version | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed" }
$package = Get-ChildItem $out -Filter "Meitou.References.*.nupkg" | Select-Object -First 1
if ($null -eq $package) { throw "no package was written to $out" }
Write-Host "references $($package.FullName)"
