-- SimpleHello sample addon for the Flux prototype
-- This addon demonstrates registering events, creating a frame and
-- attaching common scripts (OnClick, OnEnter, OnLeave, OnUpdate),
-- and using a couple small helper functions exposed by the runtime.

local addonName = "SimpleHello"
print(addonName .. ": loaded")

-- Saved variables example (Flux exposes a SavedVariables table)
local sv = SavedVariables or {}
sv.runs = (sv.runs or 0) + 1

-- Register a handler for PLAYER_LOGIN (example of event registration)
if WoW ~= nil then
  WoW.RegisterEvent("PLAYER_LOGIN", function()
    print(addonName .. ": PLAYER_LOGIN received (runs=" .. tostring(sv.runs) .. ")")
  end)
else
  print(addonName .. ": WoW API not available in this environment")
end

-- Create a simple visual frame and set it up
local f = WoW.CreateFrame()
if f ~= nil then
  f:SetSize(200, 80)
  -- Place slightly left/above center for visibility
  f:SetPoint("CENTER", "UIParent", "CENTER", -100, -50)
  -- Visual helpers added to the runtime: SetBackdrop and SetFontSize
  -- Example: use a nine-patch texture called "textures/frame9.png" with 8px insets.
  -- The path is resolved relative to the addon folder when run by Flux.
  f:SetBackdrop({ texture = "textures/frame9.png", ninepatch = { left = 8, right = 8, top = 8, bottom = 8 }, tile = false })
  f:SetFontSize(16)
  f:Show()

  f:SetScript("OnClick", function()
    print(addonName .. ": frame clicked")
  end)

  f:SetScript("OnEnter", function()
    print(addonName .. ": mouse entered frame")
  end)

  f:SetScript("OnLeave", function()
    print(addonName .. ": mouse left frame")
  end)

  local __update_acc = 0.0
  f:SetScript("OnUpdate", function(self, dt)
    -- dt is seconds since last update; throttle logging to once per second
    if dt and dt > 0 then
      __update_acc = __update_acc + dt
      if __update_acc >= 1.0 then
        __update_acc = __update_acc % 1.0
        print(string.format("%s: OnUpdate (acc=%.3f)", addonName, __update_acc))
      end
    end
  end)
end

print(addonName .. ": initialization complete")
