-- SimpleHello sample addon for Flux prototype
print("Flux sample addon: loaded")

-- Demonstrate WoW API stub usage
if WoW ~= nil then
  WoW.RegisterEvent("PLAYER_LOGIN", function()
    print("PLAYER_LOGIN received in addon")
  end)
else
  print("WoW API not available")
end
local f = WoW.CreateFrame()
f:SetSize(200,80)
f:SetPoint("CENTER", "UIParent", "CENTER", -100, -50) -- anchor form
f:Show()
f:SetScript("OnClick", function() print("Clicked!") end)
print("Flux sample addon: initialization complete")
