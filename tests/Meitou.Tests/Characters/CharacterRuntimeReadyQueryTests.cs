using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Meitou.Data.Characters;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Rendering.Gpu;

namespace Meitou.Tests.Characters;

public sealed class CharacterRuntimeReadyQueryTests
{
    sealed class Fixture
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public readonly CharacterRenderer Renderer = (CharacterRenderer)RuntimeHelpers.GetUninitializedObject(typeof(CharacterRenderer));
        public readonly CharacterDrawList List = new();
        public readonly Dictionary<CharacterAppearance, CharacterAsset?> Native = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<(CharacterAppearance Appearance, RuntimeCharacterGeometry Geometry), CharacterAsset?> Runtime = [];
        public Fixture()
        {
            Set("assets", Native); Set("runtimeAssets", Runtime); Set("<Draws>k__BackingField", List);
            List.Context = Context(); List.PreparedQuery = Renderer.IsVariantPrepared;
        }
        void Set(string name, object value) => typeof(CharacterRenderer).GetField(name, Private)!.SetValue(Renderer, value);
        public void Compile(RuntimeCharacterShaderSet shader)
        {
            var field = typeof(CharacterRenderer).GetField("runtimePrograms", Private)!;
            var cache = field.GetValue(Renderer);
            if (cache is null) { cache = Activator.CreateInstance(field.FieldType, new object?[] { null, null })!; field.SetValue(Renderer, cache); }
            var entries = (IDictionary)field.FieldType.GetField("entries", Private)!.GetValue(cache)!;
            var value = RuntimeHelpers.GetUninitializedObject(field.FieldType.GetGenericArguments()[0]);
            entries.Add(shader, value);
        }
    }
    static IGpuContext Context() => (IGpuContext)RuntimeHelpers.GetUninitializedObject(typeof(GpuContext));
    static CharacterAppearance Look() => new() { Race = null! };
    static CharacterAsset Uploaded() => (CharacterAsset)RuntimeHelpers.GetUninitializedObject(typeof(CharacterAsset));

    [Fact]
    public void Exact_runtime_readiness_cannot_be_satisfied_by_a_native_asset_another_look_or_another_variant()
    {
        var scene = new Fixture(); var look = Look(); var other = Look();
        var first = new RuntimeCharacterGeometry(); var second = new RuntimeCharacterGeometry();
        scene.Native[look] = Uploaded(); Assert.True(scene.List.IsPrepared(look));
        Assert.False(scene.List.IsPrepared(look, first));
        scene.Runtime[(look, first)] = Uploaded();
        Assert.True(scene.List.IsPrepared(look, first)); Assert.False(scene.List.IsPrepared(other, first));
        Assert.False(scene.List.IsPrepared(look, second));
        first.Retire(); Assert.False(scene.List.IsPrepared(look, first));
    }

    [Fact]
    public void A_runtime_asset_requires_its_exact_custom_program_identity_and_the_query_never_compiles_it()
    {
        var scene = new Fixture(); var look = Look(); var shaders = RuntimeCharacterShaderSources.Default();
        var first = new RuntimeCharacterGeometry(shaders: shaders);
        scene.Runtime[(look, first)] = Uploaded();
        for (int i = 0; i < 3; i++) Assert.False(scene.List.IsPrepared(look, first));
        scene.Compile(shaders); Assert.True(scene.List.IsPrepared(look, first));
        var shared = new RuntimeCharacterGeometry(shaders: shaders); scene.Runtime[(look, shared)] = Uploaded();
        Assert.True(scene.List.IsPrepared(look, shared));
        var other = new RuntimeCharacterGeometry(shaders: RuntimeCharacterShaderSources.Default()); scene.Runtime[(look, other)] = Uploaded();
        Assert.False(scene.List.IsPrepared(look, other));
    }

    [Fact]
    public void Failed_assets_remain_not_drawable_even_when_the_existing_loading_completion_query_has_finished()
    {
        var scene = new Fixture(); var look = Look(); var geometry = new RuntimeCharacterGeometry();
        scene.Native[look] = null; scene.Runtime[(look, geometry)] = null;
        Assert.True(scene.Renderer.IsPrepared(look));
        Assert.False(scene.List.IsPrepared(look)); Assert.False(scene.List.IsPrepared(look, geometry));
        scene.Native[look] = Uploaded(); scene.Runtime[(look, geometry)] = Uploaded();
        scene.List.Items.Add(new(1, look, default, 0, []) { Geometry = geometry });
        scene.List.Items.Add(new(2, look, default, 0, []) { Geometry = new() });
        Assert.False(scene.Renderer.IsPrepared(look));
        Assert.True(scene.List.IsPrepared(look)); Assert.True(scene.List.IsPrepared(look, geometry));
    }

    [Fact]
    public void An_unattached_or_disposed_list_never_queries_a_renderer_and_disposal_releases_its_callback()
    {
        var list = new CharacterDrawList(); var look = Look(); int calls = 0;
        list.PreparedQuery = (_, _) => { calls++; return true; };
        Assert.False(list.IsPrepared(look)); Assert.Equal(0, calls);
        list.Context = Context(); Assert.True(list.IsPrepared(look)); Assert.Equal(1, calls);
        list.Disposing += () => Assert.False(list.IsPrepared(look));
        list.DisposeResources();
        Assert.Null(list.PreparedQuery); Assert.Null(list.Context);
        Assert.False(list.IsPrepared(look)); Assert.Equal(1, calls);
    }
}
