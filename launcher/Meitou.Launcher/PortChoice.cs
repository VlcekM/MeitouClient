namespace Meitou.Launcher;

/// <summary>
/// What the player answers when the Mods page offers the endorsed ports of their RE_Kenshi mods (docs/launcher.md "Mods").
/// </summary>
enum PortChoice
{
    /// <summary>
    /// Nothing: not now.
    /// </summary>
    None,

    /// <summary>
    /// Open the ports' Steam workshop pages.
    /// </summary>
    Pages,

    /// <summary>
    /// Have Steam subscribe the account to the ports.
    /// </summary>
    Subscribe
}
