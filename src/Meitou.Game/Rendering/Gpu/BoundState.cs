using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// What a <see cref="CommandList"/> last bound: the pipeline and its bind point, the index buffer and the first 32 vertex bindings, so a bind of
/// what is bound already records nothing. Forgotten (<see cref="Forget"/>) at a rendering's start and at every seam crossing. One per list.
/// </summary>
sealed unsafe class BoundState
{
    /// <summary>
    /// The pipeline last bound (default: none known).
    /// </summary>
    internal Pipeline Pipeline;

    /// <summary>
    /// The bind point <see cref="Pipeline"/> was bound at.
    /// </summary>
    internal PipelineBindPoint Point;

    /// <summary>
    /// The index buffer last bound (default: none known).
    /// </summary>
    internal GpuBufferBinding Index;

    /// <summary>
    /// The index type <see cref="Index"/> was bound with.
    /// </summary>
    internal IndexKind IndexType;

    /// <summary>
    /// The buffer last bound at each of the first 32 vertex bindings.
    /// </summary>
    readonly Buffer[] _vertex = new Buffer[32];

    /// <summary>
    /// The offset last bound at each of the first 32 vertex bindings.
    /// </summary>
    readonly ulong[] _vertexOffset = new ulong[32];

    /// <summary>
    /// A bit per vertex binding whose entries are known.
    /// </summary>
    uint _vertexValid;

    /// <summary>
    /// Forgets the pipeline, the index buffer and the vertex bindings.
    /// </summary>
    public void Forget()
    {
        Pipeline = default;
        Index = default;
        _vertexValid = 0;
    }

    /// <summary>
    /// Gathers the arguments of a vertex buffer bind into <paramref name="buffers"/> and <paramref name="offsets"/> and keeps them as bound; true
    /// when every binding was bound so already (the bind can be left out).
    /// </summary>
    public bool SameVertexBuffers(VulkanDevice device, uint first, ReadOnlySpan<GpuBufferBinding> bindings, Buffer* buffers, ulong* offsets)
    {
        int n = bindings.Length;
        if (first + (uint)n <= 32)
        {
            // One pass: compare with what is bound and gather the arguments; record only when a binding differs or is not known.
            uint mask = (n == 32 ? uint.MaxValue : (1u << n) - 1) << (int)first;
            bool same = (_vertexValid & mask) == mask;
            for (int i = 0; i < n; i++)
            {
                ref readonly var bb = ref bindings[i];
                buffers[i] = device.BufferAt(bb.Buffer.Index);
                offsets[i] = bb.Offset;
                uint b = first + (uint)i;
                if (_vertex[b].Handle != buffers[i].Handle || _vertexOffset[b] != bb.Offset)
                {
                    same = false;
                    _vertex[b] = buffers[i];
                    _vertexOffset[b] = bb.Offset;
                }
            }

            if (same) return true;

            _vertexValid |= mask;
            return false;
        }

        for (int i = 0; i < n; i++)
        {
            buffers[i] = device.BufferAt(bindings[i].Buffer.Index);
            offsets[i] = bindings[i].Offset;
            uint b = first + (uint)i;
            if (b < 32)
            {
                _vertex[b] = buffers[i];
                _vertexOffset[b] = offsets[i];
                _vertexValid |= 1u << (int)b;
            }
        }

        return false;
    }
}
