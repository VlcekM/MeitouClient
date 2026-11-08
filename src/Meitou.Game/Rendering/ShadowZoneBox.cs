namespace Meitou.Rendering;

/// <summary>
/// The switch for the foliage's shadow-cascade work list tested by zone against the cascade's own planes (docs/render-shadows.md "The cascade work
/// list"; <c>--ab shadow-zone-box</c>: A on, B off). Off: the list for a cascade holds every zone within 1.2 times the shadow range, so cascade 0, a box
/// a few hundred units deep that is drawn every frame, walks all of them on the render thread and tests all of their instances in its GPU cull.
/// On: a zone whose box (with its meshes' margin) misses the cascade's planes is left out, as the colour view leaves out zones outside its frustum.
/// GPU cull only: the CPU path shares one candidate list across the cascades of a frame and keeps the old walk.
/// </summary>
public static class ShadowZoneBox
{
    public static bool Enabled { get; set; } = true;
}
