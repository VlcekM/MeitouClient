using System.Collections.Concurrent;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// Diagnostic (issue #319, <c>MEITOU_DIAG_FAULT=1</c>): logs a character part drawn with a material record that names a bindless index freed in
/// an earlier frame (its texture's image may be destroyed while the draw runs; AMD lost the device on such a read), once per material and index,
/// with what the record and its material looked like then. Any thread (the views are gathered on workers).
/// </summary>
internal sealed class StaleMaterialCheck(FreedIndices freed)
{
    /// <summary>
    /// The (material, index) pairs already logged.
    /// </summary>
    readonly ConcurrentDictionary<(PartMaterial, uint), byte> _logged = new();

    /// <summary>
    /// Checks <paramref name="part"/>'s record against the freed indices before <paramref name="frame"/>; <paramref name="epoch"/> and
    /// <paramref name="stamp"/> are the renderer's material epoch and update stamp, <paramref name="pass"/> names the draw.
    /// </summary>
    public void Check(AssetPart part, long frame, int epoch, long stamp, string pass)
    {
        var m = part.Material;
        for (int i = 0; i < CharacterShaders.TextureSlotCount; i++)
        {
            uint index = m.Record.Tex[i];
            long freedIn = freed.FreedBefore(BindlessKind.Texture2D, index, frame);
            if (freedIn == 0 || !_logged.TryAdd((m, index), 0)) continue;
            Console.WriteLine(Describe(part, i, index, freedIn, frame, epoch, stamp, pass));
        }
    }

    /// <summary>
    /// The log line for texture slot <paramref name="slot"/> of <paramref name="part"/>'s record holding <paramref name="index"/>.
    /// </summary>
    static string Describe(AssetPart part, int slot, uint index, long freedIn, long frame, int epoch, long stamp, string pass)
    {
        var m = part.Material;
        var t = slot < CharacterShaders.Slots ? m.Tex[slot] : null;
        string texture = t is null ? "no texture" : $"'{t.Name}' ({t.State}{(t.Native is null ? ", no image" : "")})";
        return $"diagnostic #319: {pass} draws character part '{part.Label}' ({m.Description}) in frame {frame} with bindless index {index} "
            + $"freed in frame {freedIn}: record slot {m.Slot}, texture slot {slot} = {texture}; record epoch {m.RecordEpoch} of {epoch}, "
            + $"material used in update {m.SlotUsed}, part collected in update {part.MaterialFrame} of {stamp}";
    }
}
