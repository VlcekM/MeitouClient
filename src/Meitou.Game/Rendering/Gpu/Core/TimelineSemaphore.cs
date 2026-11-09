using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu.Core;

/// <summary>
/// A timeline semaphore (Vulkan 1.2): a counter the GPU and the CPU raise and wait on.
/// </summary>
public sealed unsafe class TimelineSemaphore : IDisposable
{
    /// <summary>
    /// The device the semaphore is made on.
    /// </summary>
    readonly VulkanDevice _device;
    /// <summary>
    /// The semaphore; null once disposed.
    /// </summary>
    Silk.NET.Vulkan.Semaphore _semaphore;

    /// <summary>
    /// A timeline semaphore starting at <paramref name="initialValue"/>, with a debug name when one is given.
    /// </summary>
    public TimelineSemaphore(VulkanDevice device, ulong initialValue = 0, string? name = null)
    {
        this._device = device;

        var type = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = initialValue,
        };
        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &type };
        VulkanException.Check(device.Vk.CreateSemaphore(device.Device, in info, null, out _semaphore), "vkCreateSemaphore");
        if (name != null)
            device.SetName(ObjectType.Semaphore, _semaphore.Handle, name);
    }

    /// <summary>
    /// The Vulkan semaphore.
    /// </summary>
    public Silk.NET.Vulkan.Semaphore Handle => _semaphore;

    /// <summary>
    /// The current counter value.
    /// </summary>
    public ulong Value
    {
        get
        {
            _device.Vk.GetSemaphoreCounterValue(_device.Device, _semaphore, out var v);
            return v;
        }
    }

    /// <summary>
    /// Raises the counter from the CPU (must be greater than the current value).
    /// </summary>
    public void Signal(ulong value)
    {
        var info = new SemaphoreSignalInfo { SType = StructureType.SemaphoreSignalInfo, Semaphore = _semaphore, Value = value };
        VulkanException.Check(_device.Vk.SignalSemaphore(_device.Device, in info), "vkSignalSemaphore");
    }

    /// <summary>
    /// Waits until the counter reaches <paramref name="value"/>; false on timeout.
    /// </summary>
    public bool Wait(ulong value, ulong timeoutNanoseconds = ulong.MaxValue)
    {
        var sem = _semaphore;
        var v = value;
        var info = new SemaphoreWaitInfo { SType = StructureType.SemaphoreWaitInfo, SemaphoreCount = 1, PSemaphores = &sem, PValues = &v };
        var r = _device.Vk.WaitSemaphores(_device.Device, in info, timeoutNanoseconds);
        if (r == Result.Timeout) return false;

        VulkanException.Check(r, "vkWaitSemaphores");
        return true;
    }

    /// <summary>
    /// Destroys the semaphore (once).
    /// </summary>
    public void Dispose()
    {
        if (_semaphore.Handle != 0)
        {
            _device.Vk.DestroySemaphore(_device.Device, _semaphore, null);
            _semaphore = default;
        }
    }
}
