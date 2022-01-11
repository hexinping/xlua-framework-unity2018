--[[
-- added by author @ 2021/11/18 13:02:18
-- UITest模块窗口配置，要使用还需要导出到UI.Config.UIConfig.lua
--]]
-- 窗口配置
local UITest= {
	Name = UIWindowNames.UITest,
	Layer = UILayers.SceneLayer,
	Model = require "UI.UITest.Model.UITestModel",
	Ctrl =  require "UI.UITest.Controller.UITestCtrl",
	View = require "UI.UITest.View.UITestView",
	PrefabPath = "UI/Prefabs/View/UITest.prefab",
}


return {
	UITest=UITest,
}
