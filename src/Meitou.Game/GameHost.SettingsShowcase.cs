using Meitou.Rendering;
using Meitou.Rendering.Display;
using Meitou.Rendering.Gpu;

namespace Meitou.Game;

sealed partial class GameHost
{
    /// <summary>
    /// <c>--settings-showcase</c> with <c>--screenshot</c>: the Graphics page of the settings menu (Meitou UI) over the world, saved as
    /// <c>&lt;screenshot&gt;-settings-&lt;n&gt;-&lt;state&gt;.png</c>. The menu reads and writes no preset file and changes no saved setting.
    /// </summary>
    void SettingsShowcase(DebugOverlay overlay, VulkanDisplay display, GpuTexture target, int w, int h)
    {
        var values = WorldFrame.CreateSettingsPanel(overlay, gpu, render, switches: WorldFrame.LiveSwitches(o, () => gpu, () => camera, () => render));
        var menu = new SettingsMenu(player, values, session.Bindings, () => { }, new Slider("Frame limit", 0, 360, () => 150, _ => { }, "0"),
            new UserConfig()) { NoDiskWrites = true };
        // Menus started as after the launcher chose a look (docs/launcher.md "Graphics"): its keys unsaved, set from the look; made while the values are the start ones.
        var launcherLooks = new[] { ("launcher-faithful", new UserConfig { FaithfulLook = true }), ("launcher-vanilla", new UserConfig { FaithfulLook = false, LookPreset = "Vanilla colours" }) }
            .Select(x => (x.Item1, Menu: new SettingsMenu(player, values, session.Bindings, () => { }, new Slider("Frame limit", 0, 360, () => 150, _ => { }, "0"), x.Item2) { NoDiskWrites = true }))
            .ToList();
        player.SetUiMode(Meitou.Data.Ui.UiMode.Meitou);
        int n = 0;
        void Shot(string name, int tab, Action? setup = null)
        {
            menu.ShowGraphicsTab(tab);
            setup?.Invoke();
            for (int f = 0; f < 40; f++)   // the menu's open and tab animations settle
            {
                DrawWorld(w, h);
                overlay.Target = target;
                UpdateView(w, h);
                player.Draw(overlay, w, h);
                menu.Draw(w, h);
                display.EndFrame();
                Thread.Sleep(10);
            }
            display.Context.Finish();
            string output = Path.Combine(Path.GetDirectoryName(o.Screenshot!) ?? "", $"{Path.GetFileNameWithoutExtension(o.Screenshot!)}-settings-{++n}-{name}.png");
            FramebufferCapture.SavePng(display.Context, target, output, w, h);
            Console.WriteLine($"saved     {Path.GetFullPath(output)}");
        }
        string[] tabs = ["light", "sky", "colour", "water", "skin", "distances", "detail"];
        for (int i = 0; i < tabs.Length; i++) Shot(tabs[i], i);
        menu.ShowcaseOpen("Light shafts", "Ambient light", "Tone map and grade", "Draw distances", "Terrain relief");
        Shot("light-open", 0);
        Shot("colour-open", 2);
        Shot("distances-open", 5);
        Shot("detail-open", 6);
        Shot("sky-changed", 1, () => { menu.ShowcaseOpen("Heat haze", "Clouds"); menu.ShowcaseSet("Heat haze strength (1 = game)", 0.8f); });
        menu.ShowcaseSet("Heat haze strength (1 = game)", 0.5f);
        Shot("preset-changed", 2, () => { menu.ShowcaseLook(1); menu.ShowcaseSet("Night grading strength (0 = game)", 0.8f); });
        Shot("preset-list", 2, menu.ShowcaseOpenLooks);
        Shot("save-as", 2, menu.ShowcaseNaming);
        Shot("low-quality", 0, () => menu.ShowcaseTier(0));
        Shot("faithful", 0, () => { menu.ShowcaseTier(2); menu.ShowcaseFaithful(true); });
        Shot("faithful-detail", 6);
        Shot("faithful-changed", 0, () => menu.ShowcaseSwitch("Ambient occlusion", true));
        menu.ShowcaseFaithful(false);
        // The world in every look, without the menu (the temporal upscaler's history settles first).
        for (int i = 0; i < menu.ShowcaseLookCount; i++)
        {
            string name = menu.ShowcaseLook(i);
            for (int f = 0; f < Math.Max(gpu.Post!.WarmupFrames, 8); f++) { DrawWorld(w, h); display.EndFrame(); }
            DrawWorld(w, h);
            display.EndFrame();
            display.Context.Finish();
            string output = Path.Combine(Path.GetDirectoryName(o.Screenshot!) ?? "", $"{Path.GetFileNameWithoutExtension(o.Screenshot!)}-look-{i}-{name.Replace(' ', '-').ToLowerInvariant()}.png");
            FramebufferCapture.SavePng(display.Context, target, output, w, h);
            Console.WriteLine($"saved     {Path.GetFullPath(output)}");
        }
        menu.Visible = false;
        foreach (var (name, started) in launcherLooks)
        {
            started.ApplySavedLook();
            menu = started;
            Shot(name, 2);
        }
    }
}
