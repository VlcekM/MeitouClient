using Silk.NET.Vulkan;

namespace Meitou.Rendering.Vulkan.Core;

/// <summary>A timeline semaphore (Vulkan 1.2): a counter the GPU and the CPU raise and wait on.</summary>
public sealed unsafe class TimelineSemaphore : IDisposable
{
    readonly VulkanDevice device;
    Silk.NET.Vulkan.Semaphore semaphore;

    public TimelineSemaphore(VulkanDevice device, ulong initialValue = 0, string? name = null)
    {
        this.device = device;
        var type = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = initialValue,
        };
        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &type };
        VulkanException.Check(device.Vk.CreateSemaphore(device.Device, in info, null, out semaphore), "vkCreateSemaphore");
        if (name != null)
        {
            device.SetName(ObjectType.Semaphore, semaphore.Handle, name);
        }
    }

    public Silk.NET.Vulkan.Semaphore Handle => semaphore;

    /// <summary>The current counter value.</summary>
    public ulong Value
    {
        get
        {
            device.Vk.GetSemaphoreCounterValue(device.Device, semaphore, out var v);
            return v;
        }
    }

    /// <summary>Raises the counter from the CPU (must be greater than the current value).</summary>
    public void Signal(ulong value)
    {
        var info = new SemaphoreSignalInfo { SType = StructureType.SemaphoreSignalInfo, Semaphore = semaphore, Value = value };
        VulkanException.Check(device.Vk.SignalSemaphore(device.Device, in info), "vkSignalSemaphore");
    }

    /// <summary>Waits until the counter reaches <paramref name="value"/>; false on timeout.</summary>
    public bool Wait(ulong value, ulong timeoutNanoseconds = ulong.MaxValue)
    {
        var sem = semaphore;
        var v = value;
        var info = new SemaphoreWaitInfo { SType = StructureType.SemaphoreWaitInfo, SemaphoreCount = 1, PSemaphores = &sem, PValues = &v };
        var r = device.Vk.WaitSemaphores(device.Device, in info, timeoutNanoseconds);
        if (r == Result.Timeout)
        {
            return false;
        }
        VulkanException.Check(r, "vkWaitSemaphores");
        return true;
    }

    public void Dispose()
    {
        if (semaphore.Handle != 0)
        {
            device.Vk.DestroySemaphore(device.Device, semaphore, null);
            semaphore = default;
        }
    }
}
