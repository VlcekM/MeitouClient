using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Lua;
using Meitou.Modding;

namespace Meitou.Tests.Lua;

/// <summary>
/// Unit tests for the built-in <c>Meitou.Lua</c> KenshiLua compatibility host, sandbox budgets,
/// scoped require/io, MyGUI shim, and world action queuing.
/// </summary>
public sealed class KenshiLuaTests
{
    sealed class FakeWorldBridge : ILuaWorldBridge
    {
        public double? GameHours { get; set; } = 48.5;
        public long? PlayerCats { get; set; } = 2500;
        public string? SelectedName { get; set; } = "Beep";
        public (int Width, int Height) ViewSize { get; set; } = (1920, 1080);
        public LuaWorldSnapshot Scan { get; set; } = LuaWorldSnapshot.Empty;
        public LuaCameraSnap? Camera { get; set; } = new(0, 80, -140, 0, -0.422, 0.906, 0, -25, 45, 16.0 / 9.0);
        public List<LuaWorldAction> Actions { get; } = [];

        public LuaGameDataRecord? LookupGameData(string name, int? kind) =>
            string.IsNullOrWhiteSpace(name) ? null : new LuaGameDataRecord("rec:" + name, name, kind ?? 5);

        public void EnqueueAction(LuaWorldAction action) => Actions.Add(action);
    }

    [Fact]
    public void Sandbox_EnforcesBudget_And_SafePathRejectsTraversal()
    {
        List<string> logs = [];
        LuaSandbox box = new("budget-test", new LuaLimits { InstructionsPerCall = 2000 }, 42, _ => { }, logs.Add);
        box.Load("loop", "while true do end");

        Assert.True(box.Faulted);
        Assert.Equal("budget", box.FaultReason);
        Assert.Null(LuaSandbox.SafePath("C:/mods/demo", "../secret.txt"));
        Assert.Null(LuaSandbox.SafePath("C:/mods/demo", "C:/Windows/win.ini"));
    }

    [Fact]
    public void Host_StaysIdle_WhenNoEnabledModsHaveLuaScripts()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "meitou_lua_idle_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(tmp, "PlainMod"));
        try
        {
            FakeWorldBridge bridge = new();
            KenshiLuaHost host = new(bridge, new LuaHudState());
            Assert.Equal(0, host.DiscoverEnabledMods([("PlainMod", Path.Combine(tmp, "PlainMod"))]));
            Assert.False(host.HasRunningMods);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void Host_RunsKenshiLuaParitySurface_EndToEnd()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "meitou_lua_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        string modDir = Path.Combine(tmp, "SampleKL");
        Directory.CreateDirectory(Path.Combine(modDir, "scripts", "init"));
        Directory.CreateDirectory(Path.Combine(modDir, "gui", "gfx"));
        File.WriteAllBytes(Path.Combine(modDir, "gui", "gfx", "atlas.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllText(Path.Combine(modDir, "scripts", "helper.lua"), "return { ok = 'HELPER_OK' }");
        File.WriteAllText(Path.Combine(modDir, "config.txt"), "hello=world\n");
        File.WriteAllText(Path.Combine(modDir, "scripts", "init", "main.lua"), """
            local h = require('helper')
            local okEsc = pcall(function() require('../../escape') end)
            local f = io.open('config.txt', 'r')
            local cfg = f and f:read('*a') or ''
            if f then f:close() end
            local j = json.decode(json.encode({ tag = h.ok, esc = okEsc }))
            local img = MyGUI.createImageBox('ImageBox', 10, 20, 64, 32, 'klImg', 'Back')
            img:setImageInfo('atlas.png', 0, 0, 128, 64, 32, 32)
            img:setImageIndex(2)
            img:setColour(MyGUI.Colour.new(1, 0.5, 0, 1))
            local btn = MyGUI.createButton('Button', 10, 60, 120, 32, MyGUI.Align.Left, 'Back', 'klBtn')
            btn:setCaption('OPEN')
            btn:registerCallback('MouseButtonClick', function()
              local blds = getGameWorld():getBuildings()
              if #blds > 0 then blds[1]:closeDoor() end
            end)
            local runs = 0
            registerHandler('onPlayerLoadFromSerialise', function(_, gd)
              runs = gd:getNumber('runs', 0) + 1
              KenshiLua.log('LOAD:' .. runs .. ':' .. j.tag .. ':' .. tostring(j.esc) .. ':' .. cfg:gsub('%s+', ''))
            end)
            registerHandler('onPlayerSerialise', function(_, gd) gd:setNumber('runs', runs) end)
            registerHandler('onBuildingLoaded', function(b)
              KenshiLua.log('BLD:' .. b:getDoor():getId() .. ':' .. tostring(b:getFaction():isThePlayer()))
            end)
            registerHandler('onCharacterSelect', function(c)
              local rec = getGameWorld().gamedata:getDataByName('Can of Milk', itemType.ITEM)
              c:giveItem(rec, 2)
              c.medical:amputate(RobotLimbs.LEFT_ARM)
              c.medical.blood = 42
              c.medical.fed = 1.5
              KenshiLua.log('SEL:' .. c:getName() .. ':' .. rec.id)
            end)
            registerHandler('onKeyDown', function(k) KenshiLua.log('KEY:' .. k) end)
            Events.on('MyGUI::Gui::eventFrameStart', function(_)
              local count = 0
              for c in pairs(getGameWorld():getCharacterUpdateList()) do
                if c:getName() then count = count + 1 end
              end
              KenshiLua.log('FRAME:' .. count)
            end)
            """);

        try
        {
            FakeWorldBridge bridge = new()
            {
                Scan = new LuaWorldSnapshot(
                    [new LuaCharSnap(1, 1, "Beep", 1, true, 0, 0, 10, 100, false, false, false, 1,
                        [new LuaBodyPartSnap("chest", 100, 0, 100, true)], IsAlly: true, Relation: 100)],
                    [new LuaDoorSnap("d1", "gate_01", 1, true, 0, 0, 0, 1.0, "open", false, false, true)],
                    []),
            };
            LuaHudState hud = new();
            List<string> logs = [];
            KenshiLuaHost host = new(bridge, hud, log: logs.Add, stateDir: Path.Combine(tmp, "state"));

            Assert.Equal(1, host.DiscoverEnabledMods([("SampleKL", modDir)]));
            Assert.Contains("SampleKL", host.StartAll());
            Assert.True(host.HasRunningMods);

            host.Tick([new LuaGameEvent("hit", new Dictionary<string, object?> { ["damage"] = 25.0 })]);
            host.NotifyKeyDown(0x4E);
            host.DispatchHudClick("kl:SampleKL", "klBtn");

            Assert.Contains(logs, l => l.Contains("LOAD:1:HELPER_OK:false:hello=world"));
            Assert.Contains(logs, l => l.Contains("BLD:gate_01:true"));
            Assert.Contains(logs, l => l.Contains("SEL:Beep:rec:Can of Milk"));
            Assert.Contains(logs, l => l.Contains("KEY:78"));
            Assert.Contains(logs, l => l.Contains("FRAME:1"));
            Assert.Contains(hud.ImageSnapshot(), im => im.Texture.Contains("atlas.png") && im.Rect == "64 0 32 32");
            Assert.Contains(bridge.Actions, a => a.Kind == "door.set_open" && a.TargetId == "gate_01" && !a.Flag);
            Assert.Contains(bridge.Actions, a => a.Kind == "item.give" && a.TargetId == "rec:Can of Milk" && a.Amount == 2);
            Assert.Contains(bridge.Actions, a => a.Kind == "medical.amputate" && a.Amount == 0);
            Assert.Contains(bridge.Actions, a => a.Kind == "medical.set_blood" && Math.Abs(a.X - 42) < 0.01);
            Assert.Contains(bridge.Actions, a => a.Kind == "medical.add_fed" && Math.Abs(a.X - 1.5) < 0.01);

            host.Stop("SampleKL");
            Assert.False(hud.HasAny);

            Assert.True(host.Start("SampleKL").Ok);
            Assert.Contains(logs, l => l.Contains("LOAD:2:"));
            host.StopAll();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void LuaHudOverlay_OpensBindsAndDispatchesClicks_OnMyGuiScreenHost()
    {
        LuaHudState hud = new();
        (string Owner, string Id)? clicked = null;
        LuaHudOverlay overlay = new(hud, (owner, id) => clicked = (owner, id));
        MyGuiScreenHost screens = new(new());

        overlay.SyncOpen(screens);
        Assert.False(screens.IsOpen(LuaHudOverlay.ScreenId));

        Assert.True(hud.SetImage("kl:Demo", "spr0", 0.1, 0.2, 0.05, 0.05, "file:atlas.png", "0 0 32 32", "#FFFFFF", 0.9));
        Assert.True(hud.SetText("kl:Demo", "lbl0", 0.1, 0.1, 0.2, 0.04, "Hungry Bandit", "#FF5544"));
        Assert.True(hud.SetButton("kl:Demo", "btn0", 0.4, 0.4, 0.1, 0.05, "OK", "#FFFFFF"));

        overlay.SyncOpen(screens);
        Assert.True(screens.IsOpen(LuaHudOverlay.ScreenId));

        UiBindings bindings = new();
        overlay.Bind(bindings, 1920f, 1080f);
        Assert.Equal("Hungry Bandit", bindings.Text["luaTxt0"]);
        Assert.Equal("0 0 32 32", bindings.Properties["luaImg0"]["ImageRect"]);
        Assert.Equal("OK", bindings.Text["luaBtn0"]);

        overlay.HandleEvent(new MyGuiEvent(LuaHudOverlay.ScreenId, "luaBtn0", "click", ""));
        Assert.Equal(("kl:Demo", "btn0"), clicked);

        hud.ClearMod("kl:Demo");
        overlay.SyncOpen(screens);
        Assert.False(screens.IsOpen(LuaHudOverlay.ScreenId));
    }
}
