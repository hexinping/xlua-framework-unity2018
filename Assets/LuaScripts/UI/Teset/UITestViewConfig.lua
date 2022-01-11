--[[
-- added by author @ 2021/11/18 11:34:05
-- UITestView模块窗口配置，要使用还需要导出到UI.Config.UIConfig.lua
--]]
-- 窗口配置
local UITestView= {
	Name = UIWindowNames.UITestView,
	Layer = UILayers.NormalLayer,
	Model = require "UI.UITestView.Model.UITestViewModel",
	Ctrl =  require "UI.UITestView.Controller.UITestViewCtrl",
	View = require "UI.UITestView.View.UITestViewView",
	PrefabPath = "UI/Prefabs/View/UITestView.prefab",
}


return {
	UITestView=UITestView,
}
