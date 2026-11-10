using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace Meitou.Game.Rendering.Gpu.Core;

/// <summary>
/// What a lost device says about its loss (issue #319), written to the log once, before the failed call throws: the frames in flight and the video
/// memory, then the driver's fault information (VK_EXT_device_fault: a description, the faulting addresses, vendor codes) when the device has it.
/// With <c>MEITOU_DIAG_FAULT=1</c> (<see cref="Tracking"/>) each address is matched to what is or was bound there (<see cref="GpuAddressLog"/>) and
/// each frame slot names the pass the GPU reached (<see cref="PassMarkers"/>).
/// </summary>
public sealed unsafe class DeviceFault
{
    /// <summary>
    /// Diagnostic (issue #319), <c>MEITOU_DIAG_FAULT=1</c>: GPU addresses and passes are tracked for the report (a callback per bound range, two
    /// buffer fills per pass).
    /// </summary>
    public static readonly bool Tracking = Environment.GetEnvironmentVariable("MEITOU_DIAG_FAULT") == "1";

    /// <summary>
    /// The device reported on.
    /// </summary>
    readonly VulkanDevice _dev;
    /// <summary>
    /// VK_EXT_device_fault functions; null when the device does not have the extension.
    /// </summary>
    readonly ExtDeviceFault? _ext;
    /// <summary>
    /// 1 once the report was written (only the first failed call reports).
    /// </summary>
    int _reported;

    /// <summary>
    /// The report for <paramref name="dev"/>, whose allocator and frame ring exist; <paramref name="addresses"/>: its address log when tracking.
    /// </summary>
    internal DeviceFault(VulkanDevice dev, GpuAddressLog? addresses)
    {
        _dev = dev;
        Addresses = addresses;
        if (dev.HasDeviceFault && dev.Vk.TryGetDeviceExtension(dev.Instance, dev.Device, out ExtDeviceFault ext))
            _ext = ext;

        if (Tracking) Markers = new PassMarkers(dev);
    }

    /// <summary>
    /// What is and was at each GPU address; null unless <see cref="Tracking"/> and the device reports address bindings.
    /// </summary>
    public GpuAddressLog? Addresses { get; }

    /// <summary>
    /// The passes the GPU reached in each frame; null unless <see cref="Tracking"/>.
    /// </summary>
    public PassMarkers? Markers { get; }

    /// <summary>
    /// The tracking in effect, for the start of the log.
    /// </summary>
    public string Describe() =>
        $"fault info {(_ext is null ? "unavailable (no VK_EXT_device_fault)" : "on")}, "
        + $"address tracking {(Addresses is null ? "unavailable (no VK_EXT_device_address_binding_report)" : "on")}, "
        + $"pass markers {(Markers is null ? "off" : "on")}";

    /// <summary>
    /// The call <paramref name="what"/> returned a lost device: writes the report (the first time only). Never throws: the caller's own exception
    /// follows.
    /// </summary>
    public void Report(string what)
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0) return;

        var lines = new List<string>();
        try
        {
            lines.Add(Summary(what));
            Markers?.Describe(lines);
            AddFaultInfo(lines);
        }
        catch (Exception e)
        {
            lines.Add($"device loss report failed: {e.Message}");
        }

        foreach (string line in lines) Console.Error.WriteLine($"vulkan    {line}");
    }

    /// <summary>
    /// The first line: the failed call, the frames in flight and the video memory in use.
    /// </summary>
    string Summary(string what)
    {
        var (used, budget) = _dev.VideoMemory();
        return $"device lost at {what}: frame {_dev.Frames.FrameNumber} (frame {_dev.Frames.CompletedFrame} completed), video memory "
            + $"{used >> 20} of {budget >> 20} MB, {_dev.Allocator.AllocationCount} allocations";
    }

    /// <summary>
    /// The driver's fault information: its description, each faulting address (matched to objects when tracking) and each vendor code.
    /// </summary>
    void AddFaultInfo(List<string> lines)
    {
        if (_ext is null)
        {
            lines.Add("no fault information: the device has no VK_EXT_device_fault");
            return;
        }

        var counts = new DeviceFaultCountsEXT { SType = StructureType.DeviceFaultCountsExt };
        var result = _ext.GetDeviceFaultInfo(_dev.Device, &counts, null);
        if (result != Result.Success)
        {
            lines.Add($"no fault information: vkGetDeviceFaultInfoEXT returned {result}");
            return;
        }

        // The vendor's binary crash dump is not asked for (the feature is not enabled).
        counts.VendorBinarySize = 0;
        var addresses = new DeviceFaultAddressInfoEXT[counts.AddressInfoCount];
        var vendors = new DeviceFaultVendorInfoEXT[counts.VendorInfoCount];
        fixed (DeviceFaultAddressInfoEXT* pa = addresses)
        fixed (DeviceFaultVendorInfoEXT* pv = vendors)
        {
            var info = new DeviceFaultInfoEXT { SType = StructureType.DeviceFaultInfoExt, PAddressInfos = pa, PVendorInfos = pv };
            result = _ext.GetDeviceFaultInfo(_dev.Device, &counts, &info);
            lines.Add($"fault ({result}): '{Marshal.PtrToStringAnsi((nint)info.Description)}', {counts.AddressInfoCount} addresses, "
                + $"{counts.VendorInfoCount} vendor records");
        }

        for (int i = 0; i < counts.AddressInfoCount; i++) AddAddress(addresses[i], lines);
        for (int i = 0; i < counts.VendorInfoCount; i++) AddVendor(vendors[i], lines);
        if (counts.AddressInfoCount > 0 && Addresses is null)
            lines.Add("addresses not matched to objects: set MEITOU_DIAG_FAULT=1 (needs VK_EXT_device_address_binding_report)");
    }

    /// <summary>
    /// One faulting address: its kind and precision, and when tracking what is or was bound there.
    /// </summary>
    void AddAddress(DeviceFaultAddressInfoEXT a, List<string> lines)
    {
        lines.Add($"fault address 0x{a.ReportedAddress:x} ({a.AddressType}, precision 0x{a.AddressPrecision:x})");
        Addresses?.Describe(a.ReportedAddress, a.AddressPrecision, lines);
    }

    /// <summary>
    /// One vendor fault record: its description, code and data.
    /// </summary>
    static void AddVendor(DeviceFaultVendorInfoEXT v, List<string> lines) =>
        lines.Add($"vendor fault '{Marshal.PtrToStringAnsi((nint)v.Description)}', code 0x{v.VendorFaultCode:x}, data 0x{v.VendorFaultData:x}");
}
