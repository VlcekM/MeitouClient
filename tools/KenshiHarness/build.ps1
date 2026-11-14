# Builds KenshiHarness.dll (the RE_Kenshi plugin) and kh.exe (its command line) and lays out the Kenshi mod folder.
#   -Out <folder>       where to build (default: a KenshiHarness folder in the temp folder; never the repository)
#   -Install <Kenshi>   copies the mod into <Kenshi>\mods\KenshiHarness and enables it in data\mods.cfg
#   -Zig <zig.exe>      the Zig toolchain used as the C compiler (default: zig on the PATH)
param(
    [string]$Zig = "zig",
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) "KenshiHarness"),
    [string]$Install
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$mod = Join-Path $Out "KenshiHarness"
New-Item -ItemType Directory -Force $mod | Out-Null

# -fms-extensions: the plugin guards its commands with __try/__except.
$flags = @("-target", "x86_64-windows-gnu", "-O2", "-fms-extensions", "-Wall", "-Wextra", "-Werror")
$plugin = Get-ChildItem (Join-Path $root "src") -Filter *.c | ForEach-Object FullName
$client = Get-ChildItem (Join-Path $root "client") -Filter *.c | ForEach-Object FullName

& $Zig cc @flags -shared -o (Join-Path $mod "KenshiHarness.dll") @plugin (Join-Path $root "src\harness.def")
if ($LASTEXITCODE -ne 0) { throw "the plugin did not build" }

& $Zig cc @flags -o (Join-Path $mod "kh.exe") @client
if ($LASTEXITCODE -ne 0) { throw "kh did not build" }

Remove-Item (Join-Path $mod "*.pdb"), (Join-Path $mod "*.lib") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "mod\RE_Kenshi.json") $mod -Force

# An empty FCS mod (file type 16, no records): Kenshi needs a .mod for the folder to be a mod RE_Kenshi loads plugins
# from. Strings are int32 length + UTF-8 (docs/formats/fcs-mod.md).
$w = New-Object IO.BinaryWriter([IO.File]::Create((Join-Path $mod "KenshiHarness.mod")))
try {
    $w.Write([int]16); $w.Write([int]1)
    foreach ($s in @("", "MeitouClient's test harness: lets a program see and drive the game (RE_Kenshi plugin).", "", "")) {
        $b = [Text.Encoding]::UTF8.GetBytes($s); $w.Write([int]$b.Length); $w.Write($b)
    }
    $w.Write([int]0); $w.Write([int]0)
} finally { $w.Close() }

if ($Install) {
    $dest = Join-Path $Install "mods\KenshiHarness"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $mod "*") $dest -Force

    $cfg = Join-Path $Install "data\mods.cfg"
    $lines = @(if (Test-Path $cfg) { Get-Content $cfg | Where-Object { $_ -ne "" } })
    if ($lines -notcontains "KenshiHarness.mod") { Set-Content $cfg ($lines + "KenshiHarness.mod") -Encoding ascii }
    "Installed to $dest"
}
