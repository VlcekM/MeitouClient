using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Meitou.Data.Characters;
using Meitou.Rendering;
using Meitou.Rendering.Characters;

namespace Meitou.Tests.Characters;

public class CharacterPreparationTests
{
    [Fact]
    public void Preparation_requests_are_not_visible_items_and_clear_with_the_frame_without_clearing_the_distant_list()
    {
        var list = new CharacterDrawList();
        var geometry = new RuntimeCharacterGeometry();
        var instance = new CharacterInstance(42, null!, Vector3.Zero, 0, []) { Geometry = geometry };
        Assert.Empty(list.Preparations);
        list.Preparations.Add(instance);
        Assert.Empty(list.Items);
        Assert.Empty(list.Distant);
        var resident = new HashSet<RuntimeCharacterGeometry>(ReferenceEqualityComparer.Instance);
        list.CollectRuntimeGeometry(resident);
        Assert.Same(geometry, Assert.Single(resident));
        list.Distant.Add(instance);
        list.Items.Add(instance);
        list.Clear();
        Assert.Empty(list.Items);
        Assert.Empty(list.Preparations);
        Assert.Single(list.Distant);
    }

    [Fact]
    public void An_unprepared_live_shader_defers_speculative_requests_when_actual_work_has_spent_the_budget()
    {
        // No GPU/content is attached: touching a speculative asset would fail instead of silently masking wrong priority.
        var renderer = (CharacterRenderer)RuntimeHelpers.GetUninitializedObject(typeof(CharacterRenderer));
        renderer.LoadBudget = 4;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var liveType = typeof(CharacterRenderer).GetNestedType("Live", BindingFlags.NonPublic)!;
        var live = Activator.CreateInstance(liveType)!;
        liveType.GetField("Shaders")!.SetValue(live, RuntimeCharacterShaderSources.Default());
        var actors = Array.CreateInstance(liveType, 1); actors.SetValue(live, 0);
        typeof(CharacterRenderer).GetField("live", fields)!.SetValue(renderer, actors);
        typeof(CharacterRenderer).GetField("liveCount", fields)!.SetValue(renderer, 1);
        var requests = new CharacterDrawList();
        requests.Preparations.Add(new(2, null!, Vector3.Zero, 0, []) { Geometry = new(shaders: RuntimeCharacterShaderSources.Default()) });
        typeof(CharacterRenderer).GetField("<Draws>k__BackingField", fields)!.SetValue(renderer, requests);
        object[] arguments = [Vector3.Zero, 100f, 4, 0d];
        typeof(CharacterRenderer).GetMethod("PrepareAssets", fields)!.Invoke(renderer, arguments);
        Assert.Equal(4, arguments[2]);
        Assert.Equal(0d, arguments[3]);
        Assert.Empty(requests.Items);
        Assert.Single(requests.Preparations);
    }

    [Theory]
    [InlineData(4, 3, 0, false, true)]
    [InlineData(4, 4, 0, false, false)]
    [InlineData(4, 0, 4, false, false)]
    [InlineData(4, 4, 15, true, true)]
    [InlineData(4, 0, 16, true, false)]
    [InlineData(0, 10, 100, false, true)]
    public void Preparation_uses_only_the_budget_remaining_after_actual_work(int limit, int built, double milliseconds, bool covered, bool available) =>
        Assert.Equal(available, CharacterRenderer.PreparationBudgetAvailable(limit, built, milliseconds, covered));

    [Fact]
    public void Shader_preparation_residency_uses_source_identity_and_does_not_compile_an_already_ready_program_again()
    {
        int compiled = 0, retired = 0;
        var cache = new RuntimeCharacterProgramCache<object>(_ => { compiled++; return new(); }, _ => retired++);
        var source = RuntimeCharacterShaderSources.Default();
        Assert.False(cache.Contains(source));
        var first = cache.Get(source);
        Assert.True(cache.Contains(source));
        Assert.Same(first, cache.Get(source));
        Assert.Equal(1, compiled);
        var geometry = new RuntimeCharacterGeometry(shaders: source);
        var list = new CharacterDrawList();
        list.Preparations.Add(new(1, null!, Vector3.Zero, 0, []) { Geometry = geometry });
        var requests = new HashSet<RuntimeCharacterGeometry>(ReferenceEqualityComparer.Instance);
        list.CollectRuntimeGeometry(requests);
        cache.Retain(requests.Select(item => item.Shaders!).ToHashSet());
        Assert.Equal(0, retired);
        list.Clear(); geometry.Retire(); requests.Clear(); list.CollectRuntimeGeometry(requests);
        cache.Retain(requests.Select(item => item.Shaders!).ToHashSet());
        Assert.Equal(1, retired);
        Assert.False(cache.Contains(source));
    }
}
