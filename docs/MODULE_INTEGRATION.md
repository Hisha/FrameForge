# Module integration contract

FrameForge exports presentation. The consuming module owns application data, events, and behavior.
Functional export never needs to know what a value means or which AzerothCore API provides it.

## Control identities and inventory

For each authored object, FrameForge prefers its DESIGN display name. A valid name such as
`Hunt_Seal_Value` is preserved exactly. Unsupported characters become `_`, a leading digit is
prefixed with `_`, and same-name results receive deterministic `_2`, `_3`, ... suffixes in authored
order. Renaming the DESIGN object predictably changes this identity on the next export.

The authoritative mapping is `frameforge-manifest.json` → `controlInventory`. It is built by reading
the completed exported FrameXML, not by restating a naming assumption. Every row contains:

- `authoredName`: the user-facing DESIGN name;
- `sourceObject`: the stable internal project object name;
- `exportedName`: the actual FrameXML `name`;
- `type`: the actual exported XML element type;
- `parentPath`: the actual chain of named XML ancestors;
- `luaAccess`: the supported global-table lookup expression.

All inventory controls have explicit globally addressable FrameXML names. Prefer bracket lookup so
renames and unusual—but valid—identifiers are obvious:

```lua
local sealValue = _G["Hunt_Seal_Value"]
if sealValue then
    sealValue:SetText(tostring(currentValue))
end
```

Treat a missing control as an integration/version mismatch: log the missing `exportedName`, disable
only the affected feature, and compare the installed manifest with the module version. Do not guess
suffixes in Lua.

## Updating controls

Use the normal WoW 3.3.5a widget API after resolving a name from the inventory:

```lua
local label = _G["Example_Label"]
if label then label:SetText("Updated") end

local progress = _G["Example_Progress"]
if progress then progress:SetValue(65) end

local icon = _G["Example_Icon"]
if icon then icon:SetTexture("Interface\\MyModule\\example") end

local available = _G["Example_Available"]
local unavailable = _G["Example_Unavailable"]
if available and unavailable then
    if isAvailable then available:Show(); unavailable:Hide()
    else available:Hide(); unavailable:Show() end
end
```

Frames that accept interaction can receive handlers without generated application Lua:

```lua
local action = _G["Example_Action"]
if action then
    action:EnableMouse(true)
    action:SetScript("OnMouseUp", function(self, button)
        if button == "LeftButton" then MyModule_DoAction() end
    end)
end
```

Keep one module-owned refresh function. Call it when the panel opens and whenever the underlying
data changes; register the relevant game/module events in the module, not in FrameForge:

```lua
local function RefreshPanel()
    local value = _G["Example_Value"]
    if value then value:SetText(tostring(MyModule_GetValue())) end
end

local panel = _G["Example_Panel"]
if panel then panel:HookScript("OnShow", RefreshPanel) end
-- Call RefreshPanel() from the module's existing event/update path as data changes.
```

## Replacing an old presentation safely

Functional export appends the generated DESIGN root beneath the selected functional host and never
deletes authoritative controls. If the original XML also draws a presentation, both presentations
can overlap. The module must explicitly hide or stop creating only its legacy presentation controls
after the generated package is installed, while leaving controller frames, scripts, event handlers,
templates, and Lua dependencies intact. A dedicated empty visual host in the authoritative XML is
the cleanest integration boundary. FrameForge cannot infer which existing controls are behaviorally
safe to hide, so it never suppresses them silently.

Preserve the addon's existing TOC/XML/Lua load order. Initialize the generated controls only after
their FrameXML has loaded, and keep all application-specific API calls in module Lua.
