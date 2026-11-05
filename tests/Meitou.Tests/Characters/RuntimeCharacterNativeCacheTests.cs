using Meitou.Rendering;

namespace Meitou.Tests.Characters;

public class RuntimeCharacterNativeCacheTests
{
    sealed record Token(int Value);
    sealed class Rows(int marker) : IDisposable
    {
        public readonly int[] Bindings = [marker, marker + 1];
        public bool Disposed;
        public void Dispose() { Disposed = true; Array.Clear(Bindings); }
    }
    static HashSet<Token> Live(params Token[] values) => new(values, ReferenceEqualityComparer.Instance);

    [Fact]
    public void Revisions_with_one_shared_shader_do_not_accumulate_layouts_and_queued_bindings_remain_valid()
    {
        var cache = new RuntimeCharacterNativeCache<Token,Token,Rows>();
        var nativePart = new Token(-1); var nativeProgram = new Token(-2); var sharedProgram = new Token(-3);
        var ordinary = cache.Get(nativePart, nativeProgram, () => new Rows(-10));
        var queued = new Queue<(Rows Rows,int Marker)>();
        for (int revision = 0; revision < 256; revision++)
        {
            var part = new Token(revision);
            var rows = cache.Get(part, sharedProgram, () => new Rows(revision));
            queued.Enqueue((rows, revision));
            if (queued.Count > 3) queued.Dequeue();
            cache.RetainParts(Live(nativePart, part));
            Assert.Equal(2, cache.Count);
            Assert.Same(ordinary, cache.Get(nativePart, nativeProgram, () => throw new InvalidOperationException("The ordinary layout was evicted")));
            foreach (var pending in queued)
            {
                Assert.False(pending.Rows.Disposed);
                Assert.Equal(pending.Marker, pending.Rows.Bindings[0]);
            }
        }
        Assert.True(cache.RetainParts(Live(nativePart)));
        Assert.Equal(1, cache.Count);
        foreach (var pending in queued) Assert.False(pending.Rows.Disposed);
    }

    [Fact]
    public void Retiring_a_program_preserves_other_passes_and_does_not_dispose_submitted_binding_rows()
    {
        var cache = new RuntimeCharacterNativeCache<Token,Token,Rows>();
        var mesh = new Token(0); var colour = new Token(1); var depth = new Token(2); var motion = new Token(3);
        var a = cache.Get(mesh, colour, () => new Rows(10));
        var b = cache.Get(mesh, depth, () => new Rows(20));
        var c = cache.Get(mesh, motion, () => new Rows(30));
        Assert.True(cache.RetainPrograms(Live(colour, motion)));
        Assert.Equal(2, cache.Count);
        Assert.Same(a, cache.Get(mesh, colour, () => throw new InvalidOperationException()));
        Assert.Same(c, cache.Get(mesh, motion, () => throw new InvalidOperationException()));
        Assert.Equal(new[] { 20, 21 }, b.Bindings);
        Assert.False(b.Disposed);
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(a.Disposed);
        Assert.False(c.Disposed);
    }

    [Fact]
    public void Equal_valued_mesh_and_program_objects_have_distinct_native_layout_identities()
    {
        var cache = new RuntimeCharacterNativeCache<Token,Token,Rows>();
        var a = new Token(1); var b = new Token(1); var shader = new Token(2);
        var first = cache.Get(a, shader, () => new Rows(10));
        var second = cache.Get(b, shader, () => new Rows(20));
        Assert.NotSame(first, second);
        Assert.Equal(2, cache.Count);
        Assert.True(cache.RetainParts(Live(a)));
        Assert.Equal(1, cache.Count);
        Assert.Same(first, cache.Get(a, shader, () => throw new InvalidOperationException()));
    }
}
