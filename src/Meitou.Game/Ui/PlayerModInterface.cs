using Meitou.Data.Ui;
using Meitou.Modding;
using Meitou.Simulation.Editing;

namespace Meitou.Game.Ui;

/// <summary>
/// The player interface's side of the mods' <see cref="ModInterface"/> (docs/modding.md "Interface actions"): the screens they open and the build
/// window they read and choose in, through the interface's own paths (the editor's open, the overview's tabs, build mode). Host thread only.
/// </summary>
sealed class PlayerModInterface(PlayerInterface player) : IModInterfaceHost
{
    /// <summary>
    /// The highest overview tab a mod opens (4: squads).
    /// </summary>
    const int LastOverviewTab = 4;

    /// <inheritdoc/>
    public bool Open(ModScreen kind, ScreenTarget target) => kind switch
    {
        ModScreen.CharacterEditor => OpenEditor(target),
        ModScreen.Overview => OpenOverview(target.Tab),
        ModScreen.Build => OpenBuild(),
        _ => false,
    };

    /// <summary>
    /// The editor as a recruit's edit of the target's characters in the snapshot; not over an open editor or a modal screen.
    /// </summary>
    bool OpenEditor(ScreenTarget target)
    {
        if (target.Characters is not { Count: > 0 } characters || player.CharacterEditor.EditorOpen || player.FaithfulScreens.HasModal) return false;

        return player.CharacterEditor.OpenEditor(EditKind.Recruit, characters);
    }

    /// <summary>
    /// The overview on tab <paramref name="tab"/> (0 to <see cref="LastOverviewTab"/>); not over a modal screen.
    /// </summary>
    bool OpenOverview(int tab)
    {
        if (tab is < 0 or > LastOverviewTab || player.FaithfulScreens.HasModal) return false;

        player.FaithfulScreens.Open("overview");
        player.FaithfulScreens.SetTab("overview", "TabsMain", tab);
        return true;
    }

    /// <summary>
    /// Build mode on (its window open), as the B key; true when it is on after the call.
    /// </summary>
    bool OpenBuild()
    {
        if (player.Build.Active) player.FaithfulScreens.Open("build");
        else player.Build.SetBuildMode(true);

        return player.Build.Active;
    }

    /// <inheritdoc/>
    public bool IsOpen(ModScreen kind) => kind switch
    {
        ModScreen.CharacterEditor => player.CharacterEditor.EditorOpen,
        ModScreen.Overview => player.FaithfulScreens.IsOpen("overview"),
        ModScreen.Build => player.Build.Active && player.FaithfulScreens.IsOpen("build"),
        _ => false,
    };

    /// <inheritdoc/>
    public UiRect? Bounds(ModScreen kind) => kind switch
    {
        ModScreen.CharacterEditor => player.FaithfulScreens.PlacedBounds("editor"),
        ModScreen.Overview => player.FaithfulScreens.PlacedBounds("overview"),
        ModScreen.Build => player.Build.Active ? player.FaithfulScreens.PlacedBounds("build") : null,
        _ => null,
    };

    /// <inheritdoc/>
    public IReadOnlyList<UiBuildEntry> BuildEntries() => player.Build.ModEntries();

    /// <inheritdoc/>
    public string? BuildChoice => player.Build.ModChoice;

    /// <inheritdoc/>
    public bool ChooseBuild(string building) => player.Build.ModChoose(building);
}
