using System.Runtime.InteropServices;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// SPIR-V to Metal Shading Language, in process: the step between the engine's shader compiler (shaderc, which makes SPIR-V)
/// and Metal (which wants MSL). The same translator MoltenVK embeds, without the subprocess and without the CLI tool.
/// </summary>
public static unsafe class MslTranslator
{
    /// <summary>
    /// The MSL version the shaders are written for (Metal 3).
    /// </summary>
    public const uint MslVersion = 30000;

    /// <summary>
    /// The buffer index both stages' push-constant blocks take: above the flattened resource bindings
    /// (<see cref="MetalShaders.SetStride"/> × the sets, the vertex stage's from <see cref="MetalShaders.VertexBlockBase"/>),
    /// under the stage's 31-entry buffer table. The runtime pushes to it (<c>SetVertexBytes</c>/<c>SetFragmentBytes</c>).
    /// </summary>
    public const uint PushConstantBinding = 30;

    /// <summary>
    /// The buffer index the bindless table's argument buffer takes in both stages (the MSL's
    /// <c>const device spvDescriptorSetN&amp; [[buffer(n)]]</c>, moved from the set's number): the top of the buffer
    /// table, above the flattened sets and below nothing else. The runtime binds the slot's buffer there
    /// (<c>MetalCommandList.BindBindlessTable</c>).
    /// </summary>
    public const uint TableBinding = 28;

    /// <summary>
    /// An argument buffer's parameter in the generated MSL (<c>spvDescriptorSetBufferN</c>), with its buffer index.
    /// </summary>
    static readonly System.Text.RegularExpressions.Regex ArgumentBuffer =
        new(@"constant (spvDescriptorSetBuffer(\d+)& [^ ]+) \[\[buffer\([0-9]+\)\]\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The push constant block's parameter in the generated MSL (the engine's <c>Push</c> block), with its buffer index.
    /// </summary>
    static readonly System.Text.RegularExpressions.Regex PushBlock =
        new(@"constant Push& ([_A-Za-z0-9]+) \[\[buffer\([0-9]+\)\]\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The MSL for one SPIR-V module, with its entry point renamed (the MSL entry point names have to be unique per stage:
    /// spirv-cross names the stages' entry points the same). With <paramref name="discreteSet"/> that set stays plain bindings (the stage's
    /// buffer, texture and sampler indices are the set's binding numbers) while the others are argument buffers: this spirv-cross refuses a
    /// combined image sampler in a compute stage's argument buffer, and the ray-traced passes bind theirs one by one.
    /// </summary>
    public static string Translate(byte[] spirv, string entryPoint, ExecutionModel stage, uint mslVersion = MslVersion,
        uint? argumentBufferSet = null, uint? discreteSet = null)
    {
        ArgumentNullException.ThrowIfNull(spirv);
        var cross = Cross.GetApi();

        Context* context = null;
        cross.ContextCreate(&context);

        var words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        fixed (uint* module = words)
        {
            ParsedIr* parsed = null;
            if (cross.ContextParseSpirv(context, module, (nuint)words.Length, &parsed) != Result.Success)
                throw new InvalidOperationException($"spirv-cross could not parse the module: {Error(cross, context)}");

            Compiler* compiler = null;
            if (cross.ContextCreateCompiler(context, Backend.Msl, parsed, CaptureMode.Copy, &compiler) != Result.Success)
                throw new InvalidOperationException($"spirv-cross could not make a compiler: {Error(cross, context)}");

            CompilerOptions* options = null;
            cross.CompilerCreateCompilerOptions(compiler, &options);

            // The MSL resource indices then equal the SPIR-V binding numbers, so a Metal backend can mirror the engine's
            // binding scheme instead of carrying a per-shader mapping table.
            cross.CompilerOptionsSetUint(options, CompilerOption.MslVersion, mslVersion);
            cross.CompilerOptionsSetUint(options, CompilerOption.MslPlatform, (uint)MslPlatform.Macos);
            cross.CompilerOptionsSetBool(options, CompilerOption.MslEnableDecorationBinding, 1);

            // Push constants: this spirv-cross has no binding option for them, so the block takes whatever the compiler
            // assigns (buffer 0); MetalShaders moves it to the stage's top index after the translation.
            if (argumentBufferSet is not null)
            {
                // The runtime-sized descriptor arrays (the bindless table) are Metal 3 tier-2 argument buffers; the ordinary
                // sets stay discrete (bound as plain buffers), as MoltenVK marks them. The buffer itself stays in the
                // constant address space (the default): the runtime fills its members with GPU resource ids (the layout is
                // sequential from 0 — the MSL's [[id]] decorations do not move the members; verified with the reflection).
                cross.CompilerOptionsSetUint(options, CompilerOption.MslArgumentBuffers, 1);
                cross.CompilerOptionsSetUint(options, CompilerOption.MslArgumentBuffersTier, 2);
                cross.CompilerInstallCompilerOptions(compiler, options);
                if (discreteSet is { } plain) cross.CompilerMslAddDiscreteDescriptorSet(compiler, plain);
            }
            else
                cross.CompilerInstallCompilerOptions(compiler, options);
            cross.CompilerRenameEntryPoint(compiler, "main", entryPoint, stage);

            byte* source = null;
            if (cross.CompilerCompile(compiler, &source) != Result.Success)
                throw new InvalidOperationException($"spirv-cross could not compile the module: {Error(cross, context)}");
            var msl = Marshal.PtrToStringUTF8((nint)source) ?? "";

            // Every descriptor set is an argument buffer in this mode: each takes its stage's flattened set index (the vertex stage's
            // above the vertex buffers), the table's the fixed free one.
            if (argumentBufferSet is { } table)
            {
                msl = ArgumentBuffer.Replace(msl, m =>
                {
                    uint set = uint.Parse(m.Groups[2].Value);

                    // The vertex stage's sets start above its vertex buffers; Metal's buffer table ends at 30.
                    uint index = set == table ? TableBinding
                        : stage == ExecutionModel.Vertex ? MetalShaders.VertexBlockBase + set
                        : set;
                    return $"constant {m.Groups[1].Value} [[buffer({index})]]";
                });
            }

            // This spirv-cross has no binding option for push constants: the block comes out at [[buffer(0)]] — the stage's
            // low indices belong to the flattened resources and the vertex buffers (MetalShaders's numbers), the top index
            // to the push constants. The block is identifiable by the engine's name for it (NativeShaders.PushBlock's "Push").
            msl = PushBlock.Replace(msl, $"constant Push& $1 [[buffer({PushConstantBinding})]]");

            // A ray query's ray is spirv-cross's bare `ray(...)`, which a GLSL local of that name (the probe trace's ray index) hides.
            msl = RayConstructor.Replace(msl, "raytracing::ray(");

            // A scalar buffer reference's one vector (the hit fetches' Position, TexCoord, Triangle) is read at any 4-byte offset (a 44-byte vertex
            // stride, 12-byte triangles), but spirv-cross gives it the aligned vector type: the loads were misaligned and read other bytes (every
            // cut-out candidate was rejected). Packed vectors take 4-byte alignment.
            return ReferenceVector.Replace(msl, "struct $1\n{\n    packed_$2 v;\n};");
        }
    }

    /// <summary>
    /// A struct of one vector member <c>v</c> in the generated MSL: what a GLSL scalar buffer reference to one vector becomes.
    /// </summary>
    static readonly System.Text.RegularExpressions.Regex ReferenceVector =
        new(@"struct (\w+)\n\{\n    ((?:float|uint|int)[234]) v;\n\};", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A bare <c>ray(</c> constructor in the generated MSL (not a member or an already qualified one).
    /// </summary>
    static readonly System.Text.RegularExpressions.Regex RayConstructor =
        new(@"(?<![\w:.])ray\(", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The sampler indices of a discrete set's MSL (<c>[[sampler(n)]]</c>, n the binding) made dense from 0 in the bindings' order: Metal's sampler
    /// table ends at 15, the bindings go higher. <paramref name="slots"/> gets each binding's index (-1: no sampler).
    /// </summary>
    public static string DenseSamplers(string msl, out int[] slots)
    {
        var bindings = SamplerIndex.Matches(msl).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();
        var map = new int[bindings.Length == 0 ? 0 : bindings[^1] + 1];
        Array.Fill(map, -1);
        for (int i = 0; i < bindings.Length; i++) map[bindings[i]] = i;

        slots = map;
        return SamplerIndex.Replace(msl, m => $"[[sampler({map[int.Parse(m.Groups[1].Value)]})]]");
    }

    /// <summary>
    /// A sampler's index attribute in the generated MSL.
    /// </summary>
    static readonly System.Text.RegularExpressions.Regex SamplerIndex =
        new(@"\[\[sampler\((\d+)\)\]\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The cross-compiler's last error message ("no message" when it has none).
    /// </summary>
    static string Error(Cross cross, Context* context)
    {
        byte* message = cross.ContextGetLastErrorString(context);
        return message is null ? "no message" : Marshal.PtrToStringUTF8((nint)message) ?? "no message";
    }
}
