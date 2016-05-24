﻿using System.Linq;
using ICities;
using Transit.Addon.TM.Events;
using Transit.Addon.TM.Events.Managers;
using Transit.Framework;

namespace Transit.Addon.TM.ThreadingExtensions
{
    public class NodeUpdatedThreadingExtension : ThreadingExtensionBase
    {
        public override void OnBeforeSimulationTick()
        {
            base.OnBeforeSimulationTick();

            if (NetManager.instance.m_nodesUpdated)
            {
                var updatedNodeIds = NetManager
                    .instance
                    .m_updatedNodes
                    .Where(x => x != 0)
                    .Distinct()
                    .ToArray();

                if (updatedNodeIds.Any())
                {
                    NetEventManager.instance.FireNetNodesUpdated(new NetNodesUpdatedEventArgs(updatedNodeIds));
                }
            }
        }
    }
}