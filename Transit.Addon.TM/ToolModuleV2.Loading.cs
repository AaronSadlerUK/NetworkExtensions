using System;
using System.Collections.Generic;
using ICities;
using Transit.Addon.TM.AI;
using Transit.Addon.TM.PathFindingFeatures;
using Transit.Addon.TM.Tools;
using Transit.Addon.TM.Traffic;
using Transit.Addon.TM.TrafficLight;
using Transit.Addon.TM.UI;
using Transit.Framework;
using Transit.Framework.Modularity;
using Object = UnityEngine.Object;
using Transit.Addon.TM.Data;
using Transit.Addon.TM.UI.Toolbar.RoadEditor;
using Transit.Framework.ExtensionPoints.UI;

namespace Transit.Addon.TM
{
    public partial class ToolModuleV2 : ModuleBase
    {
        public override void OnCreated(ILoading loading)
        {
            //SelfDestruct.DestructOldInstances(this);

            base.OnCreated(loading);

            // TODO: Add RoadEditorToolbarItemInfo only if needed
            TAMGameToolbarItemManager.instance.AddItem<RoadEditorToolbarItemInfo>();

            TAMRestrictionManager.instance.Init();
            TAMSpeedLimitManager.instance.Init();
            TMLaneRoutingManager.instance.Init();
            TPPLaneRoutingManager.instance.Init();

            ToolMode = Mode.Disabled;
            Detours = new List<Detour>();
            DetourInited = false;
        }

        public override void OnReleased()
        {
            base.OnReleased();

            if (ToolMode != Mode.Disabled)
            {
                ToolMode = Mode.Disabled;
                DestroyTool();
            }
        }

        public override void OnLevelLoaded(LoadMode mode)
        {
            base.OnLevelLoaded(mode);
            
            Instance = this;
            gameLoaded = false;
            if (mode == LoadMode.NewGame || mode == LoadMode.LoadGame)
            {
                gameLoaded = true;
                
                InstallTools();

                if ((ActiveOptions & Options.UseRealisticSpeeds) == Options.UseRealisticSpeeds)
                    UnitRealisticSpeedManager.Activate();

                TrafficPriority.OnLevelLoading();

                Log.Info("Adding Controls to UI.");
                UI = ToolsModifierControl.toolController.gameObject.AddComponent<TMBaseUI>();

                initDetours();
                Log.Info("OnLevelLoaded complete.");
            }
        }

        public override void OnLevelUnloading()
        {
            base.OnLevelUnloading();

            if (Instance == null)
                Instance = this;

            UninstallTools();

            if ((ActiveOptions & Options.UseRealisticSpeeds) == Options.UseRealisticSpeeds)
                UnitRealisticSpeedManager.Deactivate();

            revertDetours();
            gameLoaded = false;

            TAMRestrictionManager.instance.Reset();
            TAMSpeedLimitManager.instance.Reset();
            TMLaneRoutingManager.instance.Reset();
            TPPLaneRoutingManager.instance.Reset();

            TAMGameToolbarItemManager.instance.RemoveItem<RoadEditorToolbarItemInfo>();

            Object.Destroy(UI);
            UI = null;

            try
            {
                TrafficPriority.OnLevelUnloading();
                CustomCarAI.OnLevelUnloading();
                CustomRoadAI.OnLevelUnloading();
                CustomTrafficLights.OnLevelUnloading();
                TrafficLightSimulation.OnLevelUnloading();
                //VehicleRestrictionsManager.OnLevelUnloading();
                Flags.OnLevelUnloading();
                Translation.OnLevelUnloading();

                if (Instance != null)
                    Instance.NodeSimulationLoaded = false;
            }
            catch (Exception e)
            {
                Log.Error("Exception unloading mod. " + e.Message);
                // ignored - prevents collision with other mods
            }
        }
    }
}
