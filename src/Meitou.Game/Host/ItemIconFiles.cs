using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Ui;

namespace Meitou.Game.Host;

/// <summary>
/// The item icons Meitou draws, saved to disk and handed to the interface: their folder, the version that clears old ones, and the <c>icons</c>
/// switch's look.
/// </summary>
sealed class ItemIconFiles(GameHost host)
{
    /// <summary>
    /// Where Meitou saves the item icons it draws (<see cref="LoadCaches.Root"/>'s <c>icons</c>), named as the game's saved pictures; the supersampled ones
    /// (the <c>icons</c> switch's Meitou side) in its <c>supersampled</c> folder.
    /// </summary>
    static string IconSaveDirectory(bool enhanced) => LoadCaches.Folder("icons", enhanced ? "supersampled" : "");

    /// <summary>
    /// The look the item icons were last connected with (<see cref="SyncIconLook"/>).
    /// </summary>
    bool? _iconsEnhanced;

    /// <summary>
    /// The version of the icon drawing: it goes up when the drawing changes, and the saved icons of another version are deleted (<see
    /// cref="ClearOldIcons"/>), so pictures drawn the old way are not shown again.
    /// </summary>
    const int IconVersion = 4;

    /// <summary>
    /// Deletes the saved icons of <paramref name="directory"/> when its <c>version</c> file is not <see cref="IconVersion"/>, then writes it
    /// (failures leave the folder as it is).
    /// </summary>
    static void ClearOldIcons(string directory)
    {
        try
        {
            string stamp = Path.Combine(directory, "version");
            string current = IconVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == current) return;
            Directory.CreateDirectory(directory);
            foreach (string file in Directory.EnumerateFiles(directory, "*.png")) File.Delete(file);
            File.WriteAllText(stamp, current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The item icons of the install: the files records name, then the images the game saved under <c>data/icons</c>, then the pictures the enabled
    /// mods ship in their <c>Icons</c> folders (<see cref="ModIconFolders"/>), then the ones Meitou saved (<see cref="IconSaveDirectory"/>);
    /// supersampled (<paramref name="enhanced"/>), the game's saved pictures left out, those being drawn its way (null without game data).
    /// </summary>
    ItemIcons? ItemIconsOfInstall(bool enhanced)
    {
        if (host.Scene.Database is not { } db) return null;
        string own = IconSaveDirectory(enhanced);
        ClearOldIcons(own);

        string[] mods = ModIconFolders();
        string[] folders = enhanced ? [.. mods, own] : [Path.Combine(host.Install.DataDirectory, "icons"), .. mods, own];
        return new(db, name => host.Assets.FindInGroup(name, "Icons"), new ItemIconCache(folders));
    }

    /// <summary>
    /// The enabled mods' folders in the <c>Icons</c> resource group (a mod's <c>icons</c> folder, as <c>data/icons</c> is in resources.cfg), in
    /// the order the game registers them. The game takes a picture named as its saved ones from that group before drawing the item, so a mod's
    /// <c>icons/&lt;item&gt;.&lt;material&gt;.png</c> is the item's icon.
    /// </summary>
    string[] ModIconFolders()
    {
        string data = Path.GetFullPath(host.Install.DataDirectory) + Path.DirectorySeparatorChar;
        return
        [
            .. host.Assets.Configured.Locations
                .Where(l => l.Group == "Icons" && l.Type == "FileSystem")
                .Select(l => Path.GetFullPath(l.Path))
                .Where(p => !p.StartsWith(data, StringComparison.OrdinalIgnoreCase))
        ];
    }

    /// <summary>
    /// Once a frame: when the <c>icons</c> switch has changed, the interface's icons and the renderer's save folder follow it.
    /// </summary>
    internal void SyncIconLook()
    {
        if (host.ActiveCharacters is not { } drawer || _iconsEnhanced == drawer.EnhancedIcons) return;
        _iconsEnhanced = drawer.EnhancedIcons;
        drawer.IconSaveDirectory = IconSaveDirectory(drawer.EnhancedIcons);
        host.Player.ItemCells.SetItemIcons(ItemIconsOfInstall(drawer.EnhancedIcons));
    }

    /// <summary>
    /// Gives the interface the install's item icons and, where the game has no saved picture, the character renderer's drawing of the item and the
    /// object renderer's of a building (from build mode's parts).
    /// </summary>
    internal void ConnectItemIcons()
    {
        _iconsEnhanced = null;
        SyncIconLook();
        if (host.ActiveCharacters is null) host.Player.ItemCells.SetItemIcons(ItemIconsOfInstall(false));
        host.Player.ItemCells.SetRenderedIcons(name =>
        {
            if (UiPainter.TryParseRenderedIcon(name, out string record, out string material, out string colour))
                return host.ActiveCharacters is { } characters && characters.TryGetIcon(new IconKey(record, material, colour), out var icon) ? icon
                    : null;
            if (UiPainter.TryParseRenderedBuildingIcon(name, out string building) && host.ActiveObjects is { } objects
                    && host.Player.Build.BuildHost is { } buildHost)
                return objects.TryGetBuildingIcon(building, buildHost.Parts(building), out var picture) ? picture : null;
            return null;
        });
    }
}