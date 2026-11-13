using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Frame;
using System.Numerics;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer : ITrailImprints
{
    /// <summary>A tile of the imprint atlas in pixels and the tiles across it (one tile per mark pressed this frame).</summary>
    const int ImprintTile = 128;
    public const int ImprintColumns = 8;
    /// <summary>How high above a mark's floor the body still counts as touching the ground (world units): the sole and no more, the foot's sides and the ankle stand wider than what presses.</summary>
    const float ImprintBand = 1.3f;

    GpuTexture? _imprintColour, _imprintDepth;
    uint _imprintIndex;

    public uint ImprintAtlas => _imprintIndex;

    /// <summary>
    /// The posed mesh of each request's character, as worn (bare feet, boots, a stick leg, a paw), seen from under the ground in an orthographic tile round the mark: its depth over the
    /// band above <see cref="TrailImprint.Floor"/> is what presses the ground. Tile i is request i's; a character that is not drawn leaves its tile empty. Returns how many got a tile.
    /// </summary>
    public int RenderImprints(IReadOnlyList<TrailImprint> requests)
    {
        int count = Math.Min(requests.Count, ImprintColumns * ImprintColumns);
        if (count == 0) return 0;
        if (_imprintDepth is null)
        {
            int side = ImprintTile * ImprintColumns;
            _imprintColour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, side, side, Use: TextureUse.ColourTarget, Name: "trail imprint colour"));
            _imprintDepth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent32F, side, side, Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: "trail imprints"));
            _imprintIndex = Gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(StudioNearest(), _imprintDepth.View()));
        }
        var cmd = Gpu.BeginNative("trail imprints");
        var target = PassTargets.Of(_imprintColour, _imprintDepth);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        cmd.Clear(true, default, true, 1, new PixelRect(0, 0, (uint)target.Width, (uint)target.Height));
        int was = _portraitLive;
        try
        {
            for (int k = 0; k < count; k++)
            {
                var r = requests[k];
                int index = -1;
                for (int i = 0; i < _liveCount && index < 0; i++)
                    if (_live[i].Key == r.Key) index = i;
                if (index < 0) continue;
                int x = k % ImprintColumns * ImprintTile, y = k / ImprintColumns * ImprintTile;
                var rect = new PixelRect(x, y, ImprintTile, ImprintTile);
                Gpu.SetPassViewport(new PixelViewport(x, y, ImprintTile, ImprintTile, 0, 1), rect);
                float h = 1 / r.Half, b = 1 / ImprintBand;
                // Clip x and y are the world's x and z, clip depth the height over the floor: the lowest of the body nearest.
                var matrix = Matrix4x4.CreateTranslation(-r.Centre.X, -r.Floor, -r.Centre.Y) * new Matrix4x4(h, 0, 0, 0, 0, 0, b, 0, 0, h, 0, 0, 0, 0, 0, 1);
                var eye = new Vector3(r.Centre.X, r.Floor, r.Centre.Y);
                _portraitLive = index;
                DrawView(new ViewConstants { ViewProjection = matrix, Eye = eye, FogDistance = StudioDepth }, eye, []);
            }
        }
        finally { _portraitLive = was; }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
        return count;
    }

    void DisposeImprints()
    {
        if (_imprintDepth is not null) Gpu.Bindless.Free(BindlessKind.Texture2D, _imprintIndex);
        _imprintColour?.Dispose();
        _imprintDepth?.Dispose();
        (_imprintColour, _imprintDepth, _imprintIndex) = (null, null, 0);
    }
}
