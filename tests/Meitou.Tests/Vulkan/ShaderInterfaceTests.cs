using Meitou.Game.Rendering.Gpu.Shaders;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// The location injection on hand-written shaders: qualifiers, arrays, matrices, comments, several names per declaration.
/// </summary>
public class ShaderInterfaceTests
{
    static readonly GlslProgramCompiler Compiler = new(new ShaderCompileOptions { UseDiskCache = false });

    [Fact]
    [Slow]
    public void QualifiersArraysMatricesAndMultipleNames()
    {
        string vs = """
            #version 330 core
            layout(location = 0) in vec3 aPos;
            // out vec4 vCommented;
            /* out vec4 vBlockCommented; */
            flat out int vFlat;
            smooth out vec2 vA, vB;
            noperspective centroid out vec3 vNoPersp;
            out mat3 vMat;
            out vec4 vArr[3];
            out float vLast;
            void helper(out float o) { o = 1.0; }
            void main()
            {
                vFlat = 1; vA = vec2(0.0); vB = vec2(1.0); vNoPersp = vec3(0.0); vMat = mat3(1.0);
                vArr[0] = vec4(0.0); vArr[1] = vec4(0.0); vArr[2] = vec4(0.0); vLast = 0.0;
                gl_Position = vec4(aPos, 1.0);
            }
            """;
        string fs = """
            #version 330 core
            // in vec4 vCommented;
            in float vLast;
            in vec4 vArr[3];
            in mat3 vMat;
            flat in int vFlat;
            in vec3 vNoPersp;
            in vec2 vB;
            in vec2 vA;
            out vec4 colour;
            out vec4 normal;
            void main() { colour = vec4(vA, vB) + vArr[1] + vec4(vMat[1], vLast) + vec4(vNoPersp, float(vFlat)); normal = vec4(0.0); }
            """;
        var p = Compiler.Compile(vs, fs);

        var outs = p.Vertex.Outputs.ToDictionary(o => o.Name);
        Assert.Equal(["vA", "vArr", "vB", "vFlat", "vLast", "vMat", "vNoPersp"], outs.Keys.Order().ToArray());
        Assert.Equal(3, outs["vMat"].Columns);
        Assert.Equal(3, outs["vMat"].Slots);
        Assert.Equal(3, outs["vArr"].ArrayLength);
        Assert.Equal(ScalarKind.Int, outs["vFlat"].Kind);
        foreach (var i in p.Fragment.Inputs)
            Assert.Equal(outs[i.Name].Location, i.Location);
        Assert.Equal(7, p.Fragment.Inputs.Count);

        // no overlapping ranges
        var taken = p.Vertex.Outputs.SelectMany(o => Enumerable.Range(o.Location, o.Slots)).ToList();
        Assert.Equal(taken.Count, taken.Distinct().Count());

        Assert.Equal(["colour", "normal"], p.Fragment.Outputs.OrderBy(o => o.Location).Select(o => o.Name).ToArray());
        Assert.Equal([0, 1], p.Fragment.Outputs.Select(o => o.Location).Order().ToArray());
        Assert.Equal(0, p.Vertex.Inputs.Single().Location);

        // line numbers are unchanged by the rewrite
        Assert.Equal(vs.Count(c => c == '\n'), p.VertexSource.Count(c => c == '\n'));
    }
}
