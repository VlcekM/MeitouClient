using MoonSharp.Interpreter;

namespace Meitou.Lua;

/// <summary>
/// Clean-room 1:1 KenshiLua compatibility API bindings backed by <see cref="ILuaWorldBridge"/>.
/// </summary>
public static class KenshiLuaApi
{
    /// <summary>
    /// Pure-Lua 5.2 prelude installed into each KenshiLua mod sandbox.
    /// </summary>
    public const string PreludeLua = """
-- Lua 5.1 compatibility shims + safe wall clock & mod-scoped read-only io.open
unpack = unpack or table.unpack
bit = bit or bit32
os = os or {}
os.clock = function() return __kl_clock() end
os.date = function(fmt) return __kl_date(fmt) end
os.time = function() return math.floor(__kl_unix_time()) end
os.execute = function() return nil, "sandbox", 1 end
io = io or {}
io.open = function(path, mode)
  if mode ~= nil and mode ~= "r" and mode ~= "rb" then return nil, "read-only" end
  local content = __kl_read_mod_file(tostring(path or ""))
  if content == nil then return nil, "not found" end
  local pos = 1
  return {
    read = function(_, fmt)
      if pos > #content then return nil end
      if fmt == "*a" or fmt == "a" then
        local s = content:sub(pos); pos = #content + 1; return s
      end
      local nl = content:find("\n", pos, true)
      if nl then
        local line = content:sub(pos, nl - 1):gsub("\r$", "")
        pos = nl + 1
        return line
      end
      local s = content:sub(pos):gsub("\r$", ""); pos = #content + 1; return s
    end,
    lines = function(self)
      return function() return self:read("*l") end
    end,
    close = function() return true end,
  }
end

-- KenshiLua lektor (handle container) shim
lektor = lektor or {}
function lektor.new(_)
  local list = {}
  function list:push(v) self[#self + 1] = v end
  function list:size() return #self end
  return list
end

-- Common KenshiLua enums
itemType = itemType or {
  BUILDING = 1, CHARACTER = 2, WEAPON = 3, ARMOUR = 4, ITEM = 5,
  ANIMAL_CHARACTER = 6, CONTAINER = 7, TOWN = 8, DOOR = 9,
}
SpecialTypes = SpecialTypes or { GATE = 1, DOOR = 2, WALL = 3, HOUSE = 4 }
RobotLimbs = RobotLimbs or { LEFT_ARM = 0, RIGHT_ARM = 1, LEFT_LEG = 2, RIGHT_LEG = 3 }
LimbState = LimbState or { LIMB_ORIGINAL = 0, LIMB_STUMP = 1, LIMB_CRUSHED = 2, LIMB_REPLACED = 3 }
WaterState = WaterState or { NO_WATER = 0, SHALLOW = 1, DEEP = 2 }
GroundType = GroundType or { SAND = 0, GRASS = 1, MUD = 2, DIRT = 3, ROCK = 4 }
BuildingClassType = BuildingClassType or { NORMAL = 0, FARM = 1, WALL = 2, GATE = 3, HOUSE = 4 }

-- Event / Handler registry (registerHandler / unregisterHandler + Events.on / off)
local __kl_handlers = {}
local __kl_next_id = 0

function registerHandler(eventName, fn)
  assert(type(eventName) == "string" and type(fn) == "function", "registerHandler(eventName, fn) expects string and function")
  __kl_next_id = __kl_next_id + 1
  local id = __kl_next_id
  local list = __kl_handlers[eventName]
  if not list then list = {}; __kl_handlers[eventName] = list end
  list[#list + 1] = { id = id, fn = fn }
  return id
end

function unregisterHandler(id)
  if type(id) ~= "number" then return false end
  for _, list in pairs(__kl_handlers) do
    for i = 1, #list do
      if list[i].id == id then
        table.remove(list, i)
        return true
      end
    end
  end
  return false
end

Events = {
  on = function(ev, fn) return registerHandler(ev, fn) end,
  off = function(ev, id) return unregisterHandler(id or ev) end,
}

function __kl_dispatch(eventName, a, b, c)
  local list = __kl_handlers[eventName]
  if not list then return 0 end
  local ran = 0
  local copy = {}
  for i = 1, #list do copy[i] = list[i] end
  for i = 1, #copy do
    local ok, err = pcall(copy[i].fn, a, b, c)
    ran = ran + 1
    if not ok then KenshiLua.log("[error:" .. tostring(eventName) .. "] " .. tostring(err)) end
  end
  return ran
end

local TimeStampMT = {
  __index = {
    getTotalHours = function(self) return self._hours or 0 end,
    getTotalDays  = function(self) return math.floor((self._hours or 0) / 24) end,
    getHours      = function(self) return math.floor((self._hours or 0) % 24) end,
    getMinutes    = function(self) return math.floor(((self._hours or 0) % 1) * 60) end,
  }
}

local BodyPartMT = {
  __index = {
    maxHealth = function(self) return self.max_hp or 100 end,
  }
}

local HandleMT = {
  __index = {
    isValid = function(self)
      if self.building then return #tostring(self.building) > 0 end
      return (self.slot or 0) > 0 or (self.gen or 0) > 0
    end,
    toString = function(self)
      if self.building then return tostring(self.building) end
      return tostring(self.slot) .. ":" .. tostring(self.gen)
    end,
    getCharacter = function(self)
      local c = __kl_char_by_id(self.slot, self.gen)
      return c and __kl_wrap_char(c) or nil
    end,
    getBuilding = function(self)
      if not self.building then return nil end
      local b = __kl_building_by_id(self.building)
      return b and __kl_wrap_building(b) or nil
    end,
  }
}

function getGlobalConstants()
  return { MAX_SQUAD_SIZE = 20, MAX_FACTION_SIZE = 30 }
end

function getRootObjectFactory()
  return {
    create = function(_, data, pos) return { handle = setmetatable({ slot = 0, gen = 0 }, HandleMT), data = data, pos = pos } end,
    createRandomCharacter = function() return nil end,
  }
end

local FactionMT = {
  __index = {
    isThePlayer = function(self) return self.is_player == true end,
    getId       = function(self) return self.id or -1 end,
    isEnemy     = function(self, other)
      local oid = type(other) == "table" and other.id or other
      return __kl_faction_relation(oid) <= -30
    end,
  }
}

local function __kl_make_faction(id, isPlayer)
  return setmetatable({
    id = id or -1,
    is_player = isPlayer == true,
    relations = {
      isAlly = function(_, f) return __kl_faction_is_ally(type(f) == "table" and f.id or f) end,
      getFactionRelation = function(_, f) return __kl_faction_relation(type(f) == "table" and f.id or f) end,
    }
  }, FactionMT)
end

local ItemMT = {
  __index = {
    getModelName = function(self) return self.name or self.record or "" end,
    getName      = function(self) return self.name or self.record or "" end,
  }
}

local CharMethods = {
  getName            = function(self) return self.name end,
  getPos             = function(self) return { x = self.x, y = self.y, z = self.z } end,
  getPosition        = function(self) return { x = self.x, y = self.y, z = self.z } end,
  getFaction         = function(self) return __kl_make_faction(self.faction, self.is_player) end,
  isPlayerCharacter  = function(self) return self.is_player == true end,
  isUnconscious      = function(self) return self.unconcious == true or self.down == true end,
  isUnconcious       = function(self) return self.unconcious == true or self.down == true end,
  isLiterallyUnconciousNotPretending = function(self) return self.unconcious == true or self.down == true end,
  isDead             = function(self) return self.dead == true end,
  isCrippled         = function(self) return false end,
  isFleeing          = function(self) return false end,
  isAnimal           = function(self)
    if self.is_animal == true then return { age = self.age or 1.0 } end
    return false
  end,
  isEnemy            = function(self, other)
    if self.is_enemy == true then return true end
    if type(other) == "table" and other.is_player == true then return self.is_enemy == true end
    return false
  end,
  inCombat           = function(self) return self.in_combat == true end,
  isIndoors          = function(self) return false end,
  isUnique           = function(self) return false end,
  isATrader          = function(self) return self.is_trader == true end,
  getHealth          = function(self) return self.hp or 0 end,
  getSquadId         = function(self) return self.squad_id or -1 end,
  getWaterLevel      = function(self) return 0 end,
  getOrientation     = function(self) return { w = 1, x = 0, y = 0, z = 0 } end,
  getGameData        = function(self) return { id = self.template_id or self.name, stringID = self.template_id or "", name = self.name } end,
  getCharBody        = function(self) return { getPosition = function() return self:getPosition() end } end,
  getPlatoon         = function(self) return { getSquadSize = function() return 1 end, getCharactersInArea = function() return {} end } end,
  getAppearance      = function(self)
    if not self._appearance then self._appearance = { appearanceData = { fdata = {} } } end
    return self._appearance
  end,
  getInventory       = function(self)
    local raw = __kl_char_items(self.slot, self.gen)
    for i = 1, #raw do setmetatable(raw[i], ItemMT) end
    raw.getAllItems = function() return raw end
    return raw
  end,
  getMedical         = function(self) return self.medical end,
  say                = function(self, msg) return __kl_floater(self.x, self.y + 18, self.z, tostring(msg or "")) end,
  giveItem           = function(self, rec, count)
    local id = type(rec) == "table" and (rec.id or rec.name) or rec
    return __kl_item_give(self.slot, self.gen, tostring(id or ""), count or 1)
  end,
  removeItem         = function(self, rec, count)
    local id = type(rec) == "table" and (rec.id or rec.name) or rec
    return __kl_item_remove(self.slot, self.gen, tostring(id or ""), count or 1)
  end,
}

local CharMT = {
  __index = function(self, k)
    local m = CharMethods[k]
    if m ~= nil then return m end
    if k == "handle" then
      local h = setmetatable({ slot = self.slot, gen = self.gen }, HandleMT)
      rawset(self, "handle", h)
      return h
    end
    if k == "medical" then
      local rawParts = rawget(self, "parts") or {}
      local anat = {}
      for i = 1, #rawParts do anat[i] = setmetatable(rawParts[i], BodyPartMT) end
      local maxB = rawget(self, "max_blood") or 100
      local medStore = {
        anatomy = anat,
        unconcious = rawget(self, "unconcious") == true or rawget(self, "down") == true,
        dead = rawget(self, "dead") == true,
        blood = rawget(self, "blood") or maxB,
        hunger = 3.0,
        fed = 0.0,
        stats = { _toughness = rawget(self, "toughness") or 1, ageMult = 1 },
        getMaxBlood = function() return maxB end,
        getPartCount = function() return #anat end,
        amputate = function(_, limbEnum)
          return __kl_med_amputate(self.slot, self.gen, tonumber(limbEnum) or 0)
        end,
      }
      local med = setmetatable({}, {
        __index = medStore,
        __newindex = function(_, mk, mv)
          if mk == "blood" and type(mv) == "number" then
            medStore.blood = mv
            __kl_med_set_blood(self.slot, self.gen, mv)
          elseif mk == "fed" and type(mv) == "number" then
            local delta = mv - (medStore.fed or 0)
            medStore.fed = mv
            __kl_med_add_fed(self.slot, self.gen, delta)
          else
            medStore[mk] = mv
          end
        end
      })
      rawset(self, "medical", med)
      return med
    end
    if k == "myRace" then return { data = { name = rawget(self, "race_id") or "" } } end
    if k == "movement" then
      return { isIdle = function() return rawget(self, "in_combat") ~= true end, isStandingOnSomething = function() return nil end }
    end
    if k == "crimes" then
      local b = rawget(self, "bounty") or 0
      return { getTotalBounty = function() return b end }
    end
    if k == "data" then
      return { stringID = rawget(self, "template_id") or "", name = rawget(self, "name") or "" }
    end
    if k == "platoon" then
      if rawget(self, "is_trader") == true then return { me = { squadTemplate = "trader" } } end
      return nil
    end
    if k == "_isEngagedWithAPlayer" then
      return rawget(self, "in_combat") == true and rawget(self, "is_player") ~= true
    end
    return nil
  end
}

function __kl_wrap_char(c) return c and setmetatable(c, CharMT) or nil end

local GameDataMT = {
  __index = {
    getString = function(self, k, def) local v = self._d[k]; return v ~= nil and tostring(v) or (def or "") end,
    setString = function(self, k, v) self._d[k] = tostring(v or "") end,
    getNumber = function(self, k, def) local v = tonumber(self._d[k]); return v ~= nil and v or (def or 0) end,
    setNumber = function(self, k, v) self._d[k] = tonumber(v) or 0 end,
    getBool   = function(self, k, def) local v = self._d[k]; if v == nil then return def == true end; return v == true end,
    setBool   = function(self, k, v) self._d[k] = (v == true) end,
    has       = function(self, k) return self._d[k] ~= nil end,
    toTable   = function(self) return self._d end,
  }
}

function __kl_make_gamedata(rawJson)
  local d = (type(rawJson) == "string" and #rawJson > 0) and json.decode(rawJson) or nil
  if type(d) ~= "table" then d = {} end
  return setmetatable({ _d = d }, GameDataMT)
end

local BuildingMethods = {
  getId                         = function(self) return self.building or self.id end,
  getName                       = function(self) return self.name or self.building or self.id end,
  getPos                        = function(self) return { x = self.x, y = self.y, z = self.z } end,
  getPosition                   = function(self) return { x = self.x, y = self.y, z = self.z } end,
  getDoorPosition               = function(self) return { x = self.x, y = self.y, z = self.z } end,
  getDoor                       = function(self) return self end,
  getBuildState_ActualNonShared = function(self) return { isComplete = true } end,
  getGameData                   = function(self) return { id = self.building or self.id, stringID = self.building or self.id, name = self.name or self.building or self.id, type = itemType.BUILDING } end,
  getFaction                    = function(self) return __kl_make_faction(self.owner or -1, self.is_player) end,
  isPlayer                      = function(self) return self.is_player == true end,
  isGate                        = function(self) return self.is_gate == true end,
  isOpen                        = function(self) return (self.open_amount or 0) > 0.5 or self.motion == "open" or self.motion == "opening" end,
  isLocked                      = function(self) return self.locked == true end,
  isBroken                      = function(self) return self.broken == true end,
  openDoor                      = function(self) return __kl_door_open(self.building or self.id, true) end,
  closeDoor                     = function(self) return __kl_door_open(self.building or self.id, false) end,
  setOpen                       = function(self, v) return __kl_door_open(self.building or self.id, v == true) end,
  setLocked                     = function(self, v) return __kl_door_lock(self.building or self.id, v == true) end,
  lockDoor                      = function(self) return __kl_door_lock(self.building or self.id, true) end,
  unlockDoor                    = function(self) return __kl_door_lock(self.building or self.id, false) end,
}

local BuildingMT = {
  __index = function(self, k)
    local m = BuildingMethods[k]
    if m ~= nil then return m end
    if k == "handle" then
      local h = setmetatable({ building = self.building or self.id }, HandleMT)
      rawset(self, "handle", h)
      return h
    end
    return nil
  end
}

function __kl_wrap_building(b) return b and setmetatable(b, BuildingMT) or nil end

local CameraMT = {
  __index = {
    getPosition        = function(self) return { x = self.x, y = self.y, z = self.z } end,
    getCameraPos       = function(self) return { x = self.x, y = self.y, z = self.z } end,
    getForward         = function(self) return { x = self.fx, y = self.fy, z = self.fz } end,
    getFacingDirection = function(self) return { x = self.fx, y = self.fy, z = self.fz } end,
    getCenter          = function(self) return { x = self.x + self.fx * 20, y = self.y + self.fy * 20, z = self.z + self.fz * 20 } end,
    getYaw             = function(self) return self.yaw or 0 end,
    getPitch           = function(self) return self.pitch or 0 end,
    getFov             = function(self) return self.fov or 60 end,
    project            = function(self, x, y, z) return __kl_cam_project(x, y, z) end,
    isVisible          = function(self, pos)
      if type(pos) ~= "table" then return false end
      local p = __kl_cam_project(pos.x or 0, pos.y or 0, pos.z or 0)
      return p ~= nil and p.visible == true
    end,
  }
}

local PlayerInterfaceMT = {
  __index = function(self, k)
    if k == "money" then return __kl_player_cats() or 0 end
    if k == "selected" then return __kl_selected_name() end
    if k == "camera" then return getGameWorld():getCamera() end
    if k == "getCamera" then return function() return getGameWorld():getCamera() end end
    if k == "getCharacterEditMode" then return function() return false end end
    if k == "isEnemy" then return function(_, c) return type(c) == "table" and c.is_enemy == true end end
    if k == "playerCharacters" then
      local list = getGameWorld():getCharacterUpdateList()
      local out = {}
      for i = 1, #list do if list[i].is_player == true then out[#out + 1] = list[i] end end
      function out:size() return #self end
      return out
    end
    if k == "getAllPlayerCharacters" then return function() return self.playerCharacters end end
    if k == "selectedObject" or k == "selectedCharacter" then
      local sel = __kl_selected_name()
      if not sel then return nil end
      local list = getGameWorld():getCharacterUpdateList()
      for i = 1, #list do if list[i].name == sel then return list[i].handle end end
      return nil
    end
    if k == "getSelectedCharacters" then
      return function()
        local sel = __kl_selected_name()
        if not sel then return {} end
        local list = getGameWorld():getCharacterUpdateList()
        local out = {}
        for i = 1, #list do if list[i].name == sel then out[#out + 1] = list[i] end end
        return out
      end
    end
    if k == "getFaction" then return function() return __kl_make_faction(1, true) end end
    return nil
  end
}
local __kl_player_singleton = setmetatable({}, PlayerInterfaceMT)
function getPlayerInterface() return __kl_player_singleton end

local CharSetMT = {
  __pairs = function(self)
    local i = 0
    return function()
      i = i + 1
      local v = self[i]
      if v ~= nil then return v, true end
      return nil
    end
  end
}

local GameWorldMT = {
  __index = function(self, k)
    if k == "activeMods" then return __kl_active_mods() end
    if k == "paused" then return false end
    if k == "player" then return __kl_player_singleton end
    if k == "gamedata" then
      return {
        getDataByName = function(_, name, kind)
          if type(name) ~= "string" or #name == 0 then return nil end
          return __kl_gamedata_by_name(name, kind)
        end
      }
    end
    if k == "zoneMgr" then
      return {
        getZone = function(_, x, z) return { x = math.floor((x or 0) / 768), z = math.floor((z or 0) / 768) } end,
        getGroundType = function(_, _) return GroundType.DIRT end,
      }
    end
    if k == "getTimeStamp_inGameHours" then
      return function() return setmetatable({ _hours = __kl_game_hours() or 0 }, TimeStampMT) end
    end
    if k == "getCharacterUpdateList" then
      return function()
        local raw = __kl_chars()
        for i = 1, #raw do setmetatable(raw[i], CharMT) end
        return setmetatable(raw, CharSetMT)
      end
    end
    if k == "getBuildings" or k == "getDoors" then
      return function()
        local raw = __kl_doors()
        for i = 1, #raw do setmetatable(raw[i], BuildingMT) end
        return raw
      end
    end
    if k == "getObjectsWithinSphere" then
      return function(_, pos, radius, kind)
        local cx, cz = (pos and pos.x) or 0, (pos and (pos.z or pos.y)) or 0
        local r2 = (radius or 0) * (radius or 0)
        local out = {}
        if kind == itemType.BUILDING or kind == itemType.DOOR then
          local raw = __kl_doors()
          for i = 1, #raw do
            local d = raw[i]
            local dx, dz = d.x - cx, d.z - cz
            if dx * dx + dz * dz <= r2 then out[#out + 1] = setmetatable(d, BuildingMT) end
          end
        else
          local raw = __kl_chars()
          for i = 1, #raw do
            local c = raw[i]
            local dx, dz = c.x - cx, c.z - cz
            if dx * dx + dz * dz <= r2 then out[#out + 1] = setmetatable(c, CharMT) end
          end
        end
        return out
      end
    end
    if k == "getCamera" then
      return function()
        local c = __kl_camera()
        if not c then return nil end
        return setmetatable(c, CameraMT)
      end
    end
    if k == "isPaused" or k == "userPause" then return function() return false end end
    if k == "getFrameSpeedMultiplier" then return function() return 1.0 end end
    if k == "getAllPlayerCharacters" then return function() return __kl_player_singleton.playerCharacters end end
    if k == "getCharactersInArea" then return function(_, pos, radius) return self:getObjectsWithinSphere(pos, radius, itemType.CHARACTER) end end
    if k == "getNearestActiveCharacter" then
      return function(_, pos)
        local cx, cz = (pos and pos.x) or 0, (pos and (pos.z or pos.y)) or 0
        local raw = __kl_chars()
        local best, bestD = nil, math.huge
        for i = 1, #raw do
          local c = raw[i]
          local dx, dz = c.x - cx, c.z - cz
          local d = dx * dx + dz * dz
          if d < bestD then best, bestD = c, d end
        end
        return best and setmetatable(best, CharMT) or nil
      end
    end
    if k == "showPlayerAMessage" then return function(_, msg) KenshiLua.log(tostring(msg or "")) end end
    return nil
  end
}

local __kl_world_singleton = setmetatable({}, GameWorldMT)
function getGameWorld() return __kl_world_singleton end

SaveManager = {
  getSingleton = function()
    return {
      isLoading = function() return false end,
      isLoadingFromASaveGame = function() return false end,
      getCurrentSaveName = function() return "meitou" end,
    }
  end
}

-- Cooperative coroutine scheduler (Meitou.spawn / after / cancel / wait / waitFrames / waitUntil)
local __kl_tasks = {}
local __kl_task_next_id = 1
local __kl_task_max = 64

Meitou = {
  spawn = function(fn)
    if type(fn) ~= "function" then return nil end
    if #__kl_tasks >= __kl_task_max then
      KenshiLua.log("[error:task] task limit reached (" .. __kl_task_max .. ")")
      return nil
    end
    local id = __kl_task_next_id
    __kl_task_next_id = __kl_task_next_id + 1
    __kl_tasks[#__kl_tasks + 1] = { id = id, co = coroutine.create(fn), wait = nil, dead = false }
    return id
  end,
  after = function(seconds, fn)
    if type(fn) ~= "function" then return nil end
    local delay = tonumber(seconds) or 0
    return Meitou.spawn(function()
      if delay > 0 then Meitou.wait(delay) end
      fn()
    end)
  end,
  cancel = function(id)
    for i = 1, #__kl_tasks do
      if __kl_tasks[i].id == id and not __kl_tasks[i].dead then
        __kl_tasks[i].dead = true
        return true
      end
    end
    return false
  end,
  wait = function(seconds)
    local dur = tonumber(seconds) or 0
    if dur < 0 then dur = 0 end
    coroutine.yield({ kind = "time", rem = dur })
  end,
  waitFrames = function(frames)
    local n = math.floor(tonumber(frames) or 1)
    if n < 1 then n = 1 end
    coroutine.yield({ kind = "frames", rem = n })
  end,
  waitUntil = function(pred)
    if type(pred) ~= "function" then return end
    while not pred() do
      coroutine.yield({ kind = "frames", rem = 1 })
    end
  end,
  taskCount = function()
    local n = 0
    for i = 1, #__kl_tasks do
      if not __kl_tasks[i].dead then n = n + 1 end
    end
    return n
  end,
}

function __kl_step_tasks(dt)
  local stepDt = tonumber(dt) or 0.016
  local i = 1
  while i <= #__kl_tasks do
    local t = __kl_tasks[i]
    if t.dead or coroutine.status(t.co) == "dead" then
      table.remove(__kl_tasks, i)
    else
      local ready = true
      local w = t.wait
      if w ~= nil then
        if w.kind == "time" then
          w.rem = w.rem - stepDt
          if w.rem > 0 then ready = false end
        elseif w.kind == "frames" then
          w.rem = w.rem - 1
          if w.rem > 0 then ready = false end
        end
      end
      if ready then
        t.wait = nil
        local ok, yielded = coroutine.resume(t.co)
        if not ok then
          KenshiLua.log("[error:task] " .. tostring(yielded))
          t.dead = true
        elseif coroutine.status(t.co) == "dead" then
          t.dead = true
        elseif type(yielded) == "table" then
          t.wait = yielded
        end
      end
      if t.dead then
        table.remove(__kl_tasks, i)
      else
        i = i + 1
      end
    end
  end
end
""";

    /// <summary>
    /// Installs the KenshiLua API bindings into <paramref name="sandbox"/>: the host callbacks first, then the prelude that builds the API on them.
    /// </summary>
    public static void Install(
        LuaSandbox sandbox,
        string modId,
        string? modFolder,
        ILuaWorldBridge bridge,
        Func<IReadOnlyList<string>> activeMods,
        Func<bool> trySpendAction,
        Action<string> log)
    {
        new KenshiLuaBindings(sandbox, modId, modFolder, bridge, activeMods, trySpendAction, log).Register();

        sandbox.Load("kenshilua_api", PreludeLua);
    }
}
