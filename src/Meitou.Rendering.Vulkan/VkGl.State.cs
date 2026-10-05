using System.Numerics;
using Silk.NET.OpenGL;

namespace Meitou.Rendering.Vulkan;

public sealed unsafe partial class VkGl
{
    // GL fixed-function state, as GL defines it; turned into pipeline keys and dynamic state at the draw.
    bool depthTest, depthWrite = true, cullFace, blend, scissorTest, alphaToCoverage, depthClamp, offsetFill, offsetLine;
    DepthFunction depthFunc = DepthFunction.Less;
    TriangleFace cullMode = TriangleFace.Back;
    FrontFaceDirection frontFace = FrontFaceDirection.Ccw;
    Silk.NET.OpenGL.PolygonMode polygonMode = Silk.NET.OpenGL.PolygonMode.Fill;
    BlendingFactor blendSrc = BlendingFactor.One, blendDst = BlendingFactor.Zero;
    (bool R, bool G, bool B, bool A) colourMask = (true, true, true, true);
    float offsetFactor, offsetUnits;
    (int X, int Y, int W, int H) viewport, scissor;
    Vector4 clearColourValue;
    double clearDepthValue = 1;
    bool dynamicStateDirty = true;

    void InitState() { }

    /// <summary>Per-frame bookkeeping reset at <see cref="BeginFrame"/> (the command buffer is new, so all dynamic state is re-sent).</summary>
    void ResetFrameState()
    {
        passActive = false;
        passColour = passDepth = null;
        dynamicStateDirty = true;
        lastPipeline = default;
        boundProgram = null;
        pushEpoch++;
    }

    public void Enable(EnableCap cap) => SetCap(cap, true);
    public void Disable(EnableCap cap) => SetCap(cap, false);

    void SetCap(EnableCap cap, bool on)
    {
        switch (cap)
        {
            case EnableCap.DepthTest: depthTest = on; break;
            case EnableCap.CullFace: cullFace = on; break;
            case EnableCap.Blend: blend = on; break;
            case EnableCap.ScissorTest: scissorTest = on; break;
            case EnableCap.SampleAlphaToCoverage: alphaToCoverage = on; break;
            case EnableCap.DepthClamp: depthClamp = on; break;
            case EnableCap.PolygonOffsetFill: offsetFill = on; break;
            case EnableCap.PolygonOffsetLine: offsetLine = on; break;
            case EnableCap.Multisample or EnableCap.TextureCubeMapSeamless or EnableCap.FramebufferSrgb: break;   // always on in Vulkan / unused
            default: throw new NotSupportedException($"glEnable({cap})");
        }
    }

    public void Viewport(int x, int y, uint width, uint height) => viewport = (x, y, (int)width, (int)height);
    public void Scissor(int x, int y, uint width, uint height) => scissor = (x, y, (int)width, (int)height);
    public void DepthMask(bool flag) => depthWrite = flag;
    public void DepthFunc(DepthFunction func) => depthFunc = func;
    public void ColorMask(bool red, bool green, bool blue, bool alpha) => colourMask = (red, green, blue, alpha);
    public void BlendFunc(BlendingFactor sfactor, BlendingFactor dfactor) => (blendSrc, blendDst) = (sfactor, dfactor);
    public void CullFace(TriangleFace mode) => cullMode = mode;
    public void FrontFace(FrontFaceDirection mode) => frontFace = mode;
    public void PolygonMode(TriangleFace face, PolygonMode mode) => polygonMode = mode;
    public void PolygonOffset(float factor, float units) => (offsetFactor, offsetUnits) = (factor, units);
    public void ClearColor(float red, float green, float blue, float alpha) => clearColourValue = new Vector4(red, green, blue, alpha);
    public void ClearDepth(double depth) => clearDepthValue = depth;
    public GLEnum GetError() => GLEnum.NoError;

    public void GetInteger(GLEnum pname, out int data) => GetInteger((GetPName)pname, out data);

    public void GetInteger(GetPName pname, out int data)
    {
        data = pname switch
        {
            GetPName.DrawFramebufferBinding => (int)drawFramebuffer,
            GetPName.ReadFramebufferBinding => (int)readFramebuffer,
            GetPName.MaxCombinedTextureImageUnits => 64,
            GetPName.MaxTextureSize => (int)device.Limits.MaxImageDimension2D,
            (GetPName)GLEnum.Samples => DrawSamples(),
            (GetPName)GLEnum.MaxSamples => 8,
            _ => 0,
        };
    }

    public void GetInteger(GetPName pname, int* data)
    {
        if (pname == GetPName.Viewport)
        {
            data[0] = viewport.X; data[1] = viewport.Y; data[2] = viewport.W; data[3] = viewport.H;
            return;
        }
        GetInteger(pname, out data[0]);
    }

    int DrawSamples()
    {
        var (colour, depth) = DrawTargets();
        return colour is { } c ? c.Texture.Samples : depth is { } d ? d.Texture.Samples : 1;
    }

    public void Finish()
    {
        Flush();
        Stats.Flushes++;
    }
}
