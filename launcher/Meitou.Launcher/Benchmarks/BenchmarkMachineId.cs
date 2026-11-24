using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// Generates a stable, non-editable machine id from the probed hardware (GPU and OS family) plus a 6-hex-character hash of a per-PC seed,
/// so two PCs with identical specs get distinct ids while the same PC reproduces its id without exposing the computer name or raw OS id.
/// </summary>
static class BenchmarkMachineId
{
    /// <summary>
    /// Maximum length the reports service accepts for <c>machine.id</c> (<c>services/Meitou.Reports/Bench.cs</c>).
    /// </summary>
    const int MaxLength = 64;

    /// <summary>
    /// Hex characters appended from the per-PC hash (24 bits).
    /// </summary>
    const int SuffixHexLength = 6;

    /// <summary>
    /// Returns a generated machine id for <paramref name="hardware"/>, or empty when no hardware record is available yet.
    /// </summary>
    public static string Generate(JsonObject? hardware, BenchmarkSettings settings)
    {
        if (hardware is null) return "";

        string gpu = BenchmarkResult.Text(hardware, "gpu") ?? "";
        string osFamily = BenchmarkResult.Text(hardware, "osFamily") ?? "";
        string slug = Slug($"{gpu} {osFamily}");
        if (slug.Length == 0) slug = "pc";

        string suffix = ShortHash(PerPcSeed(settings), gpu, osFamily);
        int maxSlug = MaxLength - SuffixHexLength - 1;
        if (slug.Length > maxSlug) slug = slug[..maxSlug].TrimEnd('-');
        return $"{slug}-{suffix}";
    }

    /// <summary>
    /// Lowercase ASCII letters and digits joined by single dashes.
    /// </summary>
    static string Slug(string text)
    {
        var raw = new string([.. text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        return string.Join('-', raw.Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Six lowercase hex digits of SHA-256 over the per-PC seed and hardware descriptor.
    /// </summary>
    static string ShortHash(string seed, string gpu, string osFamily)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"meitou-machine-v1:{seed}:{gpu}:{osFamily}");
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash.AsSpan(0, SuffixHexLength / 2));
    }

    /// <summary>
    /// A deterministic per-PC identifier from the OS (never sent raw), falling back to this install's random <see cref="BenchmarkSettings.InstallId"/>.
    /// </summary>
    static string PerPcSeed(BenchmarkSettings settings)
    {
        string? osId = OperatingSystem.IsWindows() ? ReadWindowsMachineGuid()
            : OperatingSystem.IsLinux() ? ReadLinuxMachineId()
            : OperatingSystem.IsMacOS() ? ReadMacHostUuid()
            : null;
        return string.IsNullOrWhiteSpace(osId) ? settings.EnsureInstallId() : osId.Trim();
    }

    /// <summary>
    /// Reads <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c> via <c>RegGetValueW</c> (AOT-safe, no extra package).
    /// </summary>
    static string? ReadWindowsMachineGuid()
    {
        const uint rrfRtRegSz = 0x00000002;
        nint hklm = unchecked((nint)0x80000002);
        var buffer = new char[128];
        uint size = (uint)(buffer.Length * sizeof(char));
        int status = RegGetValueW(hklm, @"SOFTWARE\Microsoft\Cryptography", "MachineGuid", rrfRtRegSz, out _, buffer, ref size);
        if (status != 0 || size < sizeof(char)) return null;

        int chars = (int)(size / sizeof(char));
        return new string(buffer, 0, chars).TrimEnd('\0');
    }

    /// <summary>
    /// Reads systemd's <c>/etc/machine-id</c> (or D-Bus fallback).
    /// </summary>
    static string? ReadLinuxMachineId()
    {
        foreach (string path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } id) return id;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the macOS host UUID via <c>libc</c>'s <c>gethostuuid</c>.
    /// </summary>
    static string? ReadMacHostUuid()
    {
        var uuid = new byte[16];
        var timeout = new Timespec(0, 0);
        try
        {
            return GetHostUuid(uuid, ref timeout) == 0 ? Convert.ToHexStringLower(uuid) : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Native <c>timespec</c> passed to macOS <c>gethostuuid</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Timespec(nint Seconds, nint Nanoseconds);

    /// <summary>
    /// Win32 <c>RegGetValueW</c> import for reading the Windows MachineGuid.
    /// </summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegGetValueW")]
    static extern int RegGetValueW(nint hkey, string subKey, string value, uint flags, out uint type, [Out] char[] data, ref uint size);

    /// <summary>
    /// macOS <c>gethostuuid</c> import.
    /// </summary>
    [DllImport("libSystem.B.dylib", EntryPoint = "gethostuuid")]
    static extern int GetHostUuid([Out] byte[] id, ref Timespec wait);
}
