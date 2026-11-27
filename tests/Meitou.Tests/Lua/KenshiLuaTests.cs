using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Ui;
using Meitou.Lua;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;

namespace Meitou.Tests.Lua;

/// <summary>
/// Unit tests for the built-in <c>Meitou.Lua</c> KenshiLua compatibility host, sandbox budgets,
/// scoped require/io, MyGUI shim, and world action queuing.
/// </summary>
public sealed class KenshiLuaTests
{
    /// <summary>
    /// The sample mod's <c>scripts/init/main.lua</c>: exercises require, io, json, the MyGUI shim, the handlers and the world API.
    /// </summary>
    const string SampleMainLua = """
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
        """;

    /// <summary>
    /// A world bridge with fixed answers that records the actions the mods queue.
    /// </summary>
    sealed class FakeWorldBridge : ILuaWorldBridge
    {
        /// <summary>
        /// The game hours the bridge reports.
        /// </summary>
        public double? GameHours { get; set; } = 48.5;

        /// <summary>
        /// The player's cats the bridge reports.
        /// </summary>
        public long? PlayerCats { get; set; } = 2500;

        /// <summary>
        /// The selected character's name the bridge reports.
        /// </summary>
        public string? SelectedName { get; set; } = "Beep";

        /// <summary>
        /// The view size the bridge reports.
        /// </summary>
        public (int Width, int Height) ViewSize { get; set; } = (1920, 1080);

        /// <summary>
        /// The world snapshot the bridge reports.
        /// </summary>
        public LuaWorldSnapshot Scan { get; set; } = LuaWorldSnapshot.Empty;

        /// <summary>
        /// The camera the bridge reports.
        /// </summary>
        public LuaCameraSnap? Camera { get; set; } = new(0, 80, -140, 0, -0.422, 0.906, 0, -25, 45, 16.0 / 9.0);

        /// <summary>
        /// The actions the mods queued, in order.
        /// </summary>
        public List<LuaWorldAction> Actions { get; } = [];

        /// <summary>
        /// A record named <paramref name="name"/> of the given kind (5 when none); null for a blank name.
        /// </summary>
        public LuaGameDataRecord? LookupGameData(string name, int? kind) =>
            string.IsNullOrWhiteSpace(name) ? null : new LuaGameDataRecord("rec:" + name, name, kind ?? 5);

        /// <summary>
        /// Records <paramref name="action"/>.
        /// </summary>
        public void EnqueueAction(LuaWorldAction action) => Actions.Add(action);
    }

    /// <summary>
    /// An endless loop faults the sandbox on its budget; a path outside the mod folder is rejected.
    /// </summary>
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

    /// <summary>
    /// A mod without Lua scripts starts nothing.
    /// </summary>
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

    /// <summary>
    /// The sample mod's handlers, HUD widgets, saved state and queued world actions work through a start, a stop and a restart.
    /// </summary>
    [Fact]
    public void Host_RunsKenshiLuaParitySurface_EndToEnd()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "meitou_lua_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        string modDir = WriteSampleMod(tmp);

        try
        {
            FakeWorldBridge bridge = new() { Scan = SampleScan() };
            LuaHudState hud = new();
            List<string> logs = [];
            KenshiLuaHost host = new(bridge, hud, log: logs.Add, stateDir: Path.Combine(tmp, "state"));

            Assert.Equal(1, host.DiscoverEnabledMods([("SampleKL", modDir)]));
            Assert.Contains("SampleKL", host.StartAll());
            Assert.True(host.HasRunningMods);

            host.Tick([new LuaGameEvent("hit", new Dictionary<string, object?> { ["damage"] = 25.0 })]);
            host.NotifyKeyDown(0x4E);
            host.DispatchHudClick("kl:SampleKL", "klBtn");

            AssertSampleLogs(logs);
            AssertSampleHud(hud);
            AssertSampleActions(bridge);

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

    /// <summary>
    /// The overlay opens when a widget exists, binds its slots, hands a click to its owner and closes when the widgets are gone.
    /// </summary>
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

    /// <summary>
    /// <c>Meitou.spawn</c>, <c>waitFrames</c>, <c>waitUntil</c>, <c>after</c>, and <c>cancel</c> advance cooperatively across frames.
    /// </summary>
    [Fact]
    public void Coroutines_SupportWaitFramesWaitUntilAfterAndCancel()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "meitou_lua_co_" + Guid.NewGuid().ToString("N")[..8]);
        string modDir = Path.Combine(tmp, "CoMod");
        Directory.CreateDirectory(Path.Combine(modDir, "scripts", "init"));
        File.WriteAllText(Path.Combine(modDir, "scripts", "init", "main.lua"), """
            local gate = false
            local cancelledId = Meitou.spawn(function()
              Meitou.waitFrames(2)
              KenshiLua.log('SHOULD_NOT_RUN')
            end)
            Meitou.cancel(cancelledId)

            Meitou.spawn(function()
              KenshiLua.log('STEP:1')
              Meitou.waitFrames(2)
              KenshiLua.log('STEP:2')
              gate = true
              Meitou.waitUntil(function() return gate end)
              KenshiLua.log('STEP:3')
            end)

            Meitou.after(0, function()
              KenshiLua.log('AFTER:OK')
            end)
            """);

        try
        {
            List<string> logs = [];
            using KenshiLuaHost host = new(new FakeWorldBridge(), new LuaHudState(), log: logs.Add);
            Assert.Equal(1, host.DiscoverEnabledMods([("CoMod", modDir)]));
            Assert.Contains("CoMod", host.StartAll());

            for (int i = 0; i < 6; i++) host.Tick();

            Assert.Contains(logs, l => l.Contains("STEP:1"));
            Assert.Contains(logs, l => l.Contains("STEP:2"));
            Assert.Contains(logs, l => l.Contains("STEP:3"));
            Assert.Contains(logs, l => l.Contains("AFTER:OK"));
            Assert.DoesNotContain(logs, l => l.Contains("SHOULD_NOT_RUN"));
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    /// <summary>
    /// Live hot-reload re-runs modified scripts on disk and preserves persisted GameData across the reload.
    /// </summary>
    [Fact]
    public void HotReload_ReloadsModifiedScript_AndPreservesGameData()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "meitou_lua_hr_" + Guid.NewGuid().ToString("N")[..8]);
        string modDir = Path.Combine(tmp, "HrMod");
        string mainPath = Path.Combine(modDir, "scripts", "init", "main.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(mainPath)!);
        File.WriteAllText(mainPath, """
            local n = 0
            registerHandler('onPlayerLoadFromSerialise', function(_, gd)
              n = gd:getNumber('count', 0) + 1
              KenshiLua.log('VER:1:' .. n)
            end)
            registerHandler('onPlayerSerialise', function(_, gd) gd:setNumber('count', n) end)
            """);

        try
        {
            List<string> logs = [];
            using KenshiLuaHost host = new(new FakeWorldBridge(), new LuaHudState(), log: logs.Add, stateDir: Path.Combine(tmp, "state"));
            Assert.Equal(1, host.DiscoverEnabledMods([("HrMod", modDir)]));
            Assert.Contains("HrMod", host.StartAll());
            Assert.Contains(logs, l => l.Contains("VER:1:1"));

            host.EnableHotReload(TimeSpan.FromMilliseconds(20));
            File.WriteAllText(mainPath, """
                local n = 0
                registerHandler('onPlayerLoadFromSerialise', function(_, gd)
                  n = gd:getNumber('count', 0) + 1
                  KenshiLua.log('VER:2:' .. n)
                end)
                registerHandler('onPlayerSerialise', function(_, gd) gd:setNumber('count', n) end)
                """);

            bool reloaded = false;
            for (int i = 0; i < 40 && !reloaded; i++)
            {
                Thread.Sleep(25);
                host.Tick();
                reloaded = logs.Any(l => l.Contains("VER:2:2"));
            }

            Assert.True(reloaded, "Expected FileSystemWatcher hot-reload to pick up VER:2 with persisted count=2.");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    /// <summary>
    /// <see cref="LuaMods.CaptureCamera"/> converts the active <see cref="WorldCamera"/> into a <see cref="LuaCameraSnap"/>
    /// that projects world targets onto screen coordinates through <see cref="SimulationLuaBridge.SetCamera"/>.
    /// </summary>
    [Fact]
    public void CaptureCamera_And_BuildCombatHitEvent_FeedLuaProjectionAndOnCombatHit()
    {
        WorldCamera cam = new()
        {
            Target = new Vector3(0, 0, 0),
            Yaw = 0f,
            Pitch = 0f,
            Distance = 100f,
            FieldOfView = 50f * MathF.PI / 180f,
        };
        LuaCameraSnap snap = LuaMods.CaptureCamera(cam, 1920, 1080);
        Assert.InRange(snap.FovY, 49.9, 50.1);
        Assert.InRange(snap.Aspect, 1.77, 1.78);
        Assert.True(snap.FwdZ < -0.99);

        CombatHit hit = new(
            Tick: 120,
            Source: CombatHitSource.Melee,
            Attacker: new CharacterId(1, 1),
            Victim: new CharacterId(2, 1),
            Sequence: 3,
            Blow: 1,
            Part: 1,
            Damage: new HitDamage(Cut: 18.5f, Blunt: 6.5f, Pierce: 0f, ExtraStun: 2f),
            Absorbed: default,
            VictimPosition: new Vector3(0, 0, 0),
            Travel: new Vector3(1, 0, 0),
            VictimYaw: 0f,
            CutDirection: 0,
            LowStrike: false,
            Intended: true,
            VictimWasAlive: true,
            VictimWasIncapacitated: false);
        LuaGameEvent ev = LuaMods.BuildCombatHitEvent(null, hit);
        Assert.Equal("hit", ev.Name);
        Assert.Equal("melee", ev.Payload["source"]);
        Assert.Equal(25.0, Convert.ToDouble(ev.Payload["damage"]));

        string tmp = Path.Combine(Path.GetTempPath(), "meitou_lua_cam_" + Guid.NewGuid().ToString("N")[..8]);
        string modDir = Path.Combine(tmp, "CamHitMod");
        Directory.CreateDirectory(Path.Combine(modDir, "scripts", "init"));
        File.WriteAllText(Path.Combine(modDir, "scripts", "init", "main.lua"), """
            registerHandler('onCombatHit', function(e)
              local p = getGameWorld():getCamera():project(e.x, e.y, e.z)
              KenshiLua.log('HIT:' .. e.source .. ':' .. math.floor(e.damage) .. ':' .. math.floor(p.x * 100) .. ':' .. math.floor(p.y * 100) .. ':' .. tostring(p.visible))
            end)
            """);
        try
        {
            SimulationLuaBridge bridge = new();
            bridge.SetViewSize(1920, 1080);
            bridge.SetCamera(snap);

            List<string> logs = [];
            KenshiLuaHost host = new(bridge, new LuaHudState(), log: logs.Add);
            Assert.Equal(1, host.DiscoverEnabledMods([("CamHitMod", modDir)]));
            Assert.Contains("CamHitMod", host.StartAll());

            host.Tick([ev]);
            Assert.Contains(logs, l => l.Contains("HIT:melee:25:50:50:true"));
            host.StopAll();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    /// <summary>
    /// Writes the sample mod (its main script, a helper, a config file and an atlas) under <paramref name="tmp"/>; its folder.
    /// </summary>
    static string WriteSampleMod(string tmp)
    {
        string modDir = Path.Combine(tmp, "SampleKL");
        Directory.CreateDirectory(Path.Combine(modDir, "scripts", "init"));
        Directory.CreateDirectory(Path.Combine(modDir, "gui", "gfx"));
        File.WriteAllBytes(Path.Combine(modDir, "gui", "gfx", "atlas.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllText(Path.Combine(modDir, "scripts", "helper.lua"), "return { ok = 'HELPER_OK' }");
        File.WriteAllText(Path.Combine(modDir, "config.txt"), "hello=world\n");
        File.WriteAllText(Path.Combine(modDir, "scripts", "init", "main.lua"), SampleMainLua);

        return modDir;
    }

    /// <summary>
    /// The world the sample mod reads: one selectable player character and one gate.
    /// </summary>
    static LuaWorldSnapshot SampleScan()
    {
        LuaBodyPartSnap[] parts = [new LuaBodyPartSnap("chest", 100, 0, 100, true)];
        LuaCharSnap character = new(1, 1, "Beep", 1, true, 0, 0, 10, 100, false, false, false, 1, parts, IsAlly: true, Relation: 100);
        LuaDoorSnap gate = new("d1", "gate_01", 1, true, 0, 0, 0, 1.0, "open", false, false, true);

        return new LuaWorldSnapshot([character], [gate], []);
    }

    /// <summary>
    /// The sample mod logged each handler's result.
    /// </summary>
    static void AssertSampleLogs(List<string> logs)
    {
        Assert.Contains(logs, l => l.Contains("LOAD:1:HELPER_OK:false:hello=world"));
        Assert.Contains(logs, l => l.Contains("BLD:gate_01:true"));
        Assert.Contains(logs, l => l.Contains("SEL:Beep:rec:Can of Milk"));
        Assert.Contains(logs, l => l.Contains("KEY:78"));
        Assert.Contains(logs, l => l.Contains("FRAME:1"));
    }

    /// <summary>
    /// The sample mod's image widget shows the atlas cell its script set.
    /// </summary>
    static void AssertSampleHud(LuaHudState hud)
    {
        Assert.Contains(hud.ImageSnapshot(), im => im.Texture.Contains("atlas.png") && im.Rect == "64 0 32 32");
    }

    /// <summary>
    /// The sample mod queued its door, item and medical actions.
    /// </summary>
    static void AssertSampleActions(FakeWorldBridge bridge)
    {
        Assert.Contains(bridge.Actions, a => a.Kind == "door.set_open" && a.TargetId == "gate_01" && !a.Flag);
        Assert.Contains(bridge.Actions, a => a.Kind == "item.give" && a.TargetId == "rec:Can of Milk" && a.Amount == 2);
        Assert.Contains(bridge.Actions, a => a.Kind == "medical.amputate" && a.Amount == 0);
        Assert.Contains(bridge.Actions, a => a.Kind == "medical.set_blood" && Math.Abs(a.X - 42) < 0.01);
        Assert.Contains(bridge.Actions, a => a.Kind == "medical.add_fed" && Math.Abs(a.X - 1.5) < 0.01);
    }
}
