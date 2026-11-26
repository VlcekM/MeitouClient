using MoonSharp.Interpreter;

namespace Meitou.Lua;

/// <summary>
/// Clean-room KenshiLua <c>MyGUI.*</c> compatibility shim mapping ImageBox/atlas, TextBox, Button,
/// Window, and child widget creation onto <see cref="LuaHudState"/>.
/// </summary>
public static class MyGuiShim
{
    #region Lua prelude

    /// <summary>
    /// Pure-Lua 5.2 shim installed into each KenshiLua sandbox.
    /// </summary>
    public const string PreludeLua = """
MyGUI = MyGUI or {}
MyGUI.Align = {
  Default = 0, Left = 1, Right = 2, HCenter = 4, Top = 8, Bottom = 16, VCenter = 32, Center = 36, Stretch = 255
}

local function __make_colour(r, g, b, a)
  return { r = r or 1, g = g or 1, b = b or 1, a = a or 1 }
end
MyGUI.Colour = setmetatable({
  new = __make_colour,
  White = __make_colour(1, 1, 1, 1),
  Black = __make_colour(0, 0, 0, 1),
  Red   = __make_colour(1, 0, 0, 1),
  Green = __make_colour(0, 1, 0, 1),
  Blue  = __make_colour(0, 0, 1, 1),
}, { __call = function(_, r, g, b, a) return __make_colour(r, g, b, a) end })

function MyGUI.getViewSize()
  return __kl_view_size()
end

MyGUI.RenderManager = {
  getInstance = function()
    return {
      getViewSize = function()
        local w, h = __kl_view_size()
        return { width = w, height = h }
      end
    }
  end
}

local function __col_hex(c)
  if type(c) == "string" then return c end
  if type(c) == "table" then
    local r = math.max(0, math.min(255, math.floor((c.r or 1) * 255 + 0.5)))
    local g = math.max(0, math.min(255, math.floor((c.g or 1) * 255 + 0.5)))
    local b = math.max(0, math.min(255, math.floor((c.b or 1) * 255 + 0.5)))
    return string.format("#%02X%02X%02X", r, g, b)
  end
  return "#FFFFFF"
end

local function __align_str(a)
  if type(a) == "string" then return a end
  if a == MyGUI.Align.Left then return "Left VCenter" end
  if a == MyGUI.Align.Right then return "Right VCenter" end
  return "Center VCenter"
end

local WidgetMT = {}
WidgetMT.__index = WidgetMT

function WidgetMT:_sync()
  if self._destroyed then return end
  if not self._visible or self._alpha <= 0.005 then
    __kl_mygui_remove(self._id)
    return
  end
  local vw, vh = __kl_view_size()
  local sx = self._x / vw
  local sy = self._y / vh
  local sw = math.max(0.001, self._w / vw)
  local sh = math.max(0.001, self._h / vh)
  if self._kind == "ImageBox" then
    if self._texture and #self._texture > 0 then
      __kl_mygui_image(self._id, sx, sy, sw, sh, self._texture, self._rect or "", self._color, self._alpha)
    end
  elseif self._kind == "Button" then
    __kl_mygui_button(self._id, sx, sy, sw, sh, self._caption, self._color, self._onClick)
  else
    __kl_mygui_text(self._id, sx, sy, sw, sh, self._caption, self._color, self._alpha, __align_str(self._align))
  end
end

function WidgetMT:setCaption(txt) self._caption = tostring(txt or ""); if self._visible then self:_sync() end end
function WidgetMT:getCaption() return self._caption end
function WidgetMT:setPosition(x, y) self._x = x or self._x; self._y = y or self._y; if self._visible then self:_sync() end end
function WidgetMT:setSize(w, h) self._w = w or self._w; self._h = h or self._h; if self._visible then self:_sync() end end
function WidgetMT:setCoord(x, y, w, h)
  self._x = x or self._x; self._y = y or self._y; self._w = w or self._w; self._h = h or self._h
  if self._visible then self:_sync() end
end
function WidgetMT:setVisible(v)
  local nv = (v == true)
  if nv == self._visible and not nv then return end
  self._visible = nv
  self:_sync()
end
function WidgetMT:getVisible() return self._visible end
function WidgetMT:setTextColour(c) self._color = __col_hex(c); if self._visible then self:_sync() end end
function WidgetMT:setColour(c) self._color = __col_hex(c); if self._visible then self:_sync() end end
function WidgetMT:setAlpha(a) self._alpha = math.max(0, math.min(1, tonumber(a) or 1)); if self._visible then self:_sync() end end
function WidgetMT:setImageTexture(tex) self._texture = tostring(tex or ""); if self._visible then self:_sync() end end
function WidgetMT:setImageCoord(x, y, w, h)
  if type(x) == "table" then
    self._rect = string.format("%d %d %d %d", math.floor(x.left or x.x or 0), math.floor(x.top or x.y or 0), math.floor(x.width or x.w or 0), math.floor(x.height or x.h or 0))
  else
    self._rect = string.format("%d %d %d %d", math.floor(x or 0), math.floor(y or 0), math.floor(w or 0), math.floor(h or 0))
  end
  if self._visible then self:_sync() end
end
function WidgetMT:setImageTile(w, h) self._tileW = w; self._tileH = h end
function WidgetMT:setImageInfo(file, x, y, sheetW, sheetH, tileW, tileH)
  self._texture = tostring(file or "")
  self._sheetX = math.floor(x or 0)
  self._sheetY = math.floor(y or 0)
  self._sheetW = math.max(1, math.floor(sheetW or tileW or 1))
  self._sheetH = math.max(1, math.floor(sheetH or tileH or 1))
  self._tileW = math.max(1, math.floor(tileW or self._sheetW))
  self._tileH = math.max(1, math.floor(tileH or self._sheetH))
  self._rect = string.format("%d %d %d %d", self._sheetX, self._sheetY, self._tileW, self._tileH)
  if self._visible then self:_sync() end
end
function WidgetMT:setImageIndex(i)
  self._imgIdx = math.max(0, math.floor(tonumber(i) or 0))
  if self._sheetW and self._tileW and self._tileW > 0 and self._tileH and self._tileH > 0 then
    local cols = math.max(1, math.floor(self._sheetW / self._tileW))
    local col = self._imgIdx % cols
    local row = math.floor(self._imgIdx / cols)
    self._rect = string.format("%d %d %d %d", (self._sheetX or 0) + col * self._tileW, (self._sheetY or 0) + row * self._tileH, self._tileW, self._tileH)
    if self._visible then self:_sync() end
  end
end
function WidgetMT:getCoord() return self._x, self._y, self._w, self._h end
function WidgetMT:setMovable(_) end
function WidgetMT:setEnabled(v) self._enabled = (v == true) end
function WidgetMT:getEnabled() return self._enabled ~= false end
function WidgetMT:createWidget(wtype, skin, x, y, w, h, a6, a7, a8)
  return MyGUI.createWidget(wtype, skin, (self._x or 0) + (x or 0), (self._y or 0) + (y or 0), w, h, a6, a7, a8)
end
function WidgetMT:setProperty(k, v)
  if k == "Caption" then self:setCaption(v)
  elseif k == "Alpha" then self:setAlpha(tonumber(v) or 1)
  elseif k == "Visible" then self:setVisible(v == true or v == "true")
  elseif k == "Colour" then self:setColour(v)
  elseif k == "TextColour" then self:setTextColour(v)
  end
end
function WidgetMT:setFontName(f) self._font = tostring(f or "") end
function WidgetMT:setTextAlign(a) self._align = a; if self._visible then self:_sync() end end
function WidgetMT:setNeedMouseFocus(_) end
function WidgetMT:setInheritsPick(_) end
function WidgetMT:getTextSize()
  local px = 16
  if self._font and self._font:find("Small", 1, true) then px = 13 end
  return math.max(8, #self._caption * math.floor(px * 0.56)), px + 2
end
function WidgetMT:destroy() self._destroyed = true; self._visible = false; __kl_mygui_remove(self._id) end
function WidgetMT:registerCallback(ev, fn)
  if (ev == "MouseButtonClick" or ev == "WindowButtonPressed") and type(fn) == "function" then
    self._onClick = fn
    if self._visible then self:_sync() end
    return true
  end
  return false
end

local __kl_wid = 0
local function __parse_tail(a6, a7, a8)
  if type(a6) == "string" then
    return MyGUI.Align.Default, a7 or "Back", a6
  end
  return a6 or MyGUI.Align.Default, a7 or "Back", a8
end

local function __make_widget(kind, x, y, w, h, align, layer, name)
  __kl_wid = __kl_wid + 1
  local id = (type(name) == "string" and #name > 0) and name or ("w" .. __kl_wid)
  return setmetatable({
    _id = id, _kind = kind, _x = x or 0, _y = y or 0, _w = w or 120, _h = h or 32,
    _caption = "", _color = (kind == "ImageBox") and "#FFFFFF" or "#F0E6D2",
    _alpha = 1, _align = align or MyGUI.Align.Center,
    _texture = nil, _rect = "", _font = "",
    _visible = true, _destroyed = false, _onClick = nil,
    layerName = layer or "Back",
  }, WidgetMT)
end

function MyGUI.createWidget(wtype, skin, x, y, w, h, a6, a7, a8)
  local align, layer, name = __parse_tail(a6, a7, a8)
  return __make_widget(wtype or "TextBox", x, y, w, h, align, layer, name)
end
function MyGUI.createWindow(skin, x, y, w, h, a6, a7, a8)
  local align, layer, name = __parse_tail(a6, a7, a8)
  return __make_widget("Window", x, y, w, h, align, layer, name)
end
function MyGUI.createTextBox(skin, x, y, w, h, a6, a7, a8)
  local align, layer, name = __parse_tail(a6, a7, a8)
  return __make_widget("TextBox", x, y, w, h, align, layer, name)
end
function MyGUI.createImageBox(skin, x, y, w, h, a6, a7, a8)
  local align, layer, name = __parse_tail(a6, a7, a8)
  return __make_widget("ImageBox", x, y, w, h, align, layer, name)
end
function MyGUI.createButton(skin, x, y, w, h, a6, a7, a8)
  local align, layer, name = __parse_tail(a6, a7, a8)
  return __make_widget("Button", x, y, w, h, align, layer, name)
end
function MyGUI.createEditBox(skin, x, y, w, h, a6, a7, a8)
  local align, layer, name = __parse_tail(a6, a7, a8)
  return __make_widget("EditBox", x, y, w, h, align, layer, name)
end
""";

    #endregion

    #region Host bindings

    /// <summary>
    /// Installs the <c>MyGUI.*</c> shim into <paramref name="sandbox"/>.
    /// </summary>
    public static void Install(
        LuaSandbox sandbox,
        string ownerKey,
        LuaHudState? hud,
        Dictionary<string, DynValue> callbacks,
        string? modFolder,
        Func<(int W, int H)> viewSize)
    {
        sandbox.Expose("", "__kl_view_size", _ => ViewSizeBinding(viewSize));
        sandbox.Expose("", "__kl_mygui_text", a => TextBinding(a, ownerKey, hud, callbacks));
        sandbox.Expose("", "__kl_mygui_image", a => ImageBinding(a, ownerKey, hud, modFolder));
        sandbox.Expose("", "__kl_mygui_button", a => ButtonBinding(a, ownerKey, hud, callbacks));
        sandbox.Expose("", "__kl_mygui_remove", a => RemoveBinding(a, ownerKey, hud, callbacks));
        sandbox.Load("kenshilua_mygui", PreludeLua);
    }

    /// <summary>
    /// <c>__kl_view_size()</c>: the view size in pixels, at least 320 by 200.
    /// </summary>
    static DynValue ViewSizeBinding(Func<(int W, int H)> viewSize)
    {
        (int w, int h) = viewSize();
        return DynValue.NewTuple(DynValue.NewNumber(Math.Max(320, w)), DynValue.NewNumber(Math.Max(200, h)));
    }

    /// <summary>
    /// <c>__kl_mygui_text(id, x, y, w, h, caption, colour, alpha?, align?)</c>: creates or updates a text widget.
    /// </summary>
    static DynValue TextBinding(CallbackArguments a, string ownerKey, LuaHudState? hud, Dictionary<string, DynValue> callbacks)
    {
        if (hud is null || a.Count < 7) return DynValue.False;

        string id = a[0].CastToString() ?? "";
        callbacks.Remove(id);
        double alpha = a.Count >= 8 && a[7].Type == DataType.Number ? a[7].Number : 1.0;
        string align = a.Count >= 9 && a[8].Type == DataType.String ? a[8].String : "Center VCenter";
        string caption = a[5].CastToString() ?? "";
        string colour = a[6].CastToString() ?? "#F0E6D2";

        return DynValue.NewBoolean(hud.SetText(ownerKey, id, a[1].Number, a[2].Number, a[3].Number, a[4].Number, caption, colour, alpha, align));
    }

    /// <summary>
    /// <c>__kl_mygui_image(id, x, y, w, h, texture, rect, colour, alpha)</c>: creates or updates an image widget.
    /// </summary>
    static DynValue ImageBinding(CallbackArguments a, string ownerKey, LuaHudState? hud, string? modFolder)
    {
        if (hud is null || a.Count < 9) return DynValue.False;

        string id = a[0].CastToString() ?? "";
        string tex = ResolveTexture(modFolder, a[5].CastToString() ?? "");
        string rect = a[6].CastToString() ?? "";
        string col = a[7].CastToString() ?? "#FFFFFF";
        double alpha = a[8].Type == DataType.Number ? a[8].Number : 1.0;

        return DynValue.NewBoolean(hud.SetImage(ownerKey, id, a[1].Number, a[2].Number, a[3].Number, a[4].Number, tex, rect, col, alpha));
    }

    /// <summary>
    /// <c>__kl_mygui_button(id, x, y, w, h, caption, colour, onClick?)</c>: creates or updates a button and remembers its click callback.
    /// </summary>
    static DynValue ButtonBinding(CallbackArguments a, string ownerKey, LuaHudState? hud, Dictionary<string, DynValue> callbacks)
    {
        if (hud is null || a.Count < 7) return DynValue.False;

        string id = a[0].CastToString() ?? "";
        string caption = a[5].CastToString() ?? "";
        string colour = a[6].CastToString() ?? "#F0E6D2";
        bool ok = hud.SetButton(ownerKey, id, a[1].Number, a[2].Number, a[3].Number, a[4].Number, caption, colour);
        if (ok && a.Count >= 8 && a[7].Type == DataType.Function) callbacks[id] = a[7];

        return DynValue.NewBoolean(ok);
    }

    /// <summary>
    /// <c>__kl_mygui_remove(id)</c>: removes a widget and its click callback.
    /// </summary>
    static DynValue RemoveBinding(CallbackArguments a, string ownerKey, LuaHudState? hud, Dictionary<string, DynValue> callbacks)
    {
        if (hud is null || a.Count < 1) return DynValue.False;

        string id = a[0].CastToString() ?? "";
        callbacks.Remove(id);

        return DynValue.NewBoolean(hud.Remove(ownerKey, id));
    }

    #endregion

    #region Texture resolution

    /// <summary>
    /// Resolves a texture name a mod gave to a <c>file:</c> path inside its folder (under <c>gui/gfx</c>, <c>gui</c>, <c>ui</c>, then the root);
    /// the name unchanged when no such file exists.
    /// </summary>
    static string ResolveTexture(string? modFolder, string rawTex)
    {
        if (string.IsNullOrWhiteSpace(rawTex)) return "";
        if (rawTex.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return rawTex;
        if (string.IsNullOrEmpty(modFolder)) return rawTex;

        string clean = rawTex.Trim().Replace('\\', '/');
        string normRoot = modFolder.TrimEnd('\\', '/').Replace('\\', '/') + "/";
        if (clean.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase)) clean = clean[normRoot.Length..];

        string[] candidates = ["gui/gfx/" + clean, "gui/" + clean, "ui/" + clean, clean];
        foreach (string rel in candidates)
        {
            string? full = LuaSandbox.SafePath(modFolder, rel);
            if (full is not null && File.Exists(full)) return "file:" + full;
        }

        return rawTex;
    }

    #endregion
}
