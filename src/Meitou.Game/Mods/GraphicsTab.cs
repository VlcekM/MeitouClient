namespace Meitou.Modding;

/// <summary>
/// A tab of the Settings screen's Graphics page a mod's setting is shown on (<see cref="SettingAttribute.Tab"/>, docs/modding.md "Settings").
/// </summary>
public enum GraphicsTab
{
    /// <summary>
    /// No tab: the setting is on the Mods page only.
    /// </summary>
    None,
    /// <summary>
    /// Lighting.
    /// </summary>
    Lighting,
    /// <summary>
    /// Water &amp; reflections.
    /// </summary>
    Water,
    /// <summary>
    /// Terrain.
    /// </summary>
    Terrain,
    /// <summary>
    /// Vegetation.
    /// </summary>
    Vegetation,
    /// <summary>
    /// Objects.
    /// </summary>
    Objects,
    /// <summary>
    /// Characters.
    /// </summary>
    Characters,
    /// <summary>
    /// Sky &amp; weather.
    /// </summary>
    Sky,
    /// <summary>
    /// Colour.
    /// </summary>
    Colour,
    /// <summary>
    /// Post FX.
    /// </summary>
    PostFx,
    /// <summary>
    /// Interface.
    /// </summary>
    Interface,
}

/// <summary>
/// On the class of an object given to <see cref="ModContext.AddSettings"/>: the Graphics tab of every setting of it that names none
/// (<see cref="SettingAttribute.Tab"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SettingsTabAttribute(GraphicsTab tab) : Attribute
{
    /// <summary>
    /// The tab the object's settings are shown on.
    /// </summary>
    public GraphicsTab Tab { get; } = tab;
}
