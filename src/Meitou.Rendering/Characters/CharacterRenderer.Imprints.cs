using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer : ITrailImprints
{
    /// <summary>A tile of the imprint atlas in pixels and the tiles across it (one tile per mark pressed this frame).</summary>
    const int ImprintTile = 128;
    public const int ImprintColumns = 8;
    /// <summary>How high above a mark's floor the body still counts as touching the ground (world units): the part of a foot that presses, a heel strike's raised toe included.</summary>
    const float ImprintBand = 2.5f;

    GpuTexture? imprintColour, imprintDepth;
    uint imprintIndex;

    public uint ImprintAtlas => imprintIndex;

    /// <summary>
    /// The posed mesh of each request's character, as worn (bare feet, boots, a stick leg, a paw), seen from under the ground in an orthographic tile round the mark: its depth over the
    /// band above <see cref="TrailImprint.Floor"/> is what presses the ground. Tile i is request i's; a character that is not drawn leaves its tile empty. Returns how many got a tile.
    /// </summary>
    public int RenderImprints(IReadOnlyList<TrailImprint> requests)
    {
        int count = Math.Min(requests.Count, ImprintColumns * ImprintColumns);
        if (count == 0) return 0;
        if (imprintDepth is null)
        {
            int side = ImprintTile * ImprintColumns;
            imprintColour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, side, side, Use: TextureUse.ColourTarget, Name: "trail imprint colour"));
            imprintDepth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent32f, side, side, Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: "trail imprints"));
            imprintIndex = Gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(StudioNearest(), imprintDepth.View()));
        }
        var cmd = Gpu.BeginNative("trail imprints");
        var target = PassTargets.Of(imprintColour, imprintDepth);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        cmd.Clear(true, default, true, 1, new PixelRect(0, 0, (uint)target.Width, (uint)target.Height));
        int was = portraitLive;
        try
        {
            for (int k = 0; k < count; k++)
            {
                var r = requests[k];
                int index = -1;
                for (int i = 0; i < liveCount && index < 0; i++)
                    if (live[i].Key == r.Key) index = i;
                if (index < 0) continue;
                int x = k % ImprintColumns * ImprintTile, y = k / ImprintColumns * ImprintTile;
                var rect = new PixelRect(x, y, ImprintTile, ImprintTile);
                Gpu.SetPassViewport(new PixelViewport(x, y, ImprintTile, ImprintTile, 0, 1), rect);
                float h = 1 / r.Half, b = 1 / ImprintBand;
                // Clip x and y are the world's x and z, clip depth the height over the floor: the lowest of the body nearest.
                var matrix = Matrix4x4.CreateTranslation(-r.Centre.X, -r.Floor, -r.Centre.Y) * new Matrix4x4(h, 0, 0, 0, 0, 0, b, 0, 0, h, 0, 0, 0, 0, 0, 1);
                var eye = new Vector3(r.Centre.X, r.Floor, r.Centre.Y);
                portraitLive = index;
                DrawView(new ViewConstants { ViewProjection = matrix, Eye = eye, FogDistance = StudioDepth }, eye, []);
            }
        }
        finally { portraitLive = was; }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
        return count;
    }

    void DisposeImprints()
    {
        if (imprintDepth is not null) Gpu.Bindless.Free(BindlessKind.Texture2D, imprintIndex);
        imprintColour?.Dispose();
        imprintDepth?.Dispose();
        (imprintColour, imprintDepth, imprintIndex) = (null, null, 0);
    }
}
