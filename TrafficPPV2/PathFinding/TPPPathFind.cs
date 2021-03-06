﻿﻿using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
using System;
using System.Collections.Generic;
using System.Threading;
using Transit.Framework.Network;
using UnityEngine;

namespace CSL_Traffic
{
    public class TPPPathFind : IExtendedPathFind
    {
        public ExtendedPathFindFacade Facade { get; set; }

        Thread IExtendedPathFind.PathFindThread
        {
            get { return m_pathFindThread; }
        }

        private struct BufferItem
        {
            public PathUnit.Position m_position;

            public float m_comparisonValue;

            public float m_methodDistance;

            public uint m_laneID;

            public NetInfo.Direction m_direction;

            public NetInfo.LaneType m_lanesUsed;
        }

        private Array32<PathUnit> m_pathUnits;

        private uint m_queueFirst;

        private uint m_queueLast;

        private uint m_calculating;

        private object m_queueLock;

        private object m_bufferLock;

        private Thread m_pathFindThread;

        private bool m_terminated;

        private int m_bufferMinPos;

        private int m_bufferMaxPos;

        private uint[] m_laneLocation;

        private PathUnit.Position[] m_laneTarget;

        private BufferItem[] m_buffer;

        private int[] m_bufferMin;

        private int[] m_bufferMax;

        private float m_maxLength;

        private uint m_startLaneA;

        private uint m_startLaneB;

        private uint m_endLaneA;

        private uint m_endLaneB;

        private uint m_vehicleLane;

        private byte m_startOffsetA;

        private byte m_startOffsetB;

        private byte m_vehicleOffset;

        private bool m_isHeavyVehicle;

        private bool m_ignoreBlocked;

        private bool m_stablePath;

        private bool m_transportVehicle;

        private Randomizer m_pathRandomizer;

        private uint m_pathFindIndex;

        private NetInfo.LaneType m_laneTypes;

        private VehicleInfo.VehicleType m_vehicleTypes;

        // TAM Extensions
	    private bool m_priorityVehicle;
		private ExtendedVehicleType m_vehicleTypeExtended = ExtendedVehicleType.Unknown;
		private readonly Dictionary<uint, ExtendedVehicleType> m_pathVehicleType = new Dictionary<uint, ExtendedVehicleType>();
        // TAM Extensions

        public void OnAwake()
        {
            this.m_laneLocation = new uint[262144];
            this.m_laneTarget = new PathUnit.Position[262144];
            this.m_buffer = new BufferItem[65536];
            this.m_bufferMin = new int[1024];
            this.m_bufferMax = new int[1024];
            this.m_queueLock = new object();
            this.m_bufferLock = Singleton<PathManager>.instance.m_bufferLock;
            this.m_pathUnits = Singleton<PathManager>.instance.m_pathUnits;
            this.m_pathFindThread = new Thread(new ThreadStart(this.PathFindThread));
            this.m_pathFindThread.Name = "Pathfind";
            this.m_pathFindThread.Priority = SimulationManager.SIMULATION_PRIORITY;
            this.m_pathFindThread.Start();
            if (!this.m_pathFindThread.IsAlive)
            {
                CODebugBase<LogChannel>.Error(LogChannel.Core, "Path find thread failed to start!");
            }
		}

        public void OnDestroy()
		{
			while (!Monitor.TryEnter(this.m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
			{
			}
			try
			{
				this.m_terminated = true;
				Monitor.PulseAll(this.m_queueLock);
			}
			finally
			{
				Monitor.Exit(this.m_queueLock);
			}
		}

	    public bool CalculatePath(uint unit, bool skipQueue)
        {
            return CalculatePath(unit, skipQueue, ExtendedVehicleType.Unknown);
        }

	    public bool CalculatePath(uint unit, bool skipQueue, ExtendedVehicleType vehicleType)
		{
			if (Singleton<PathManager>.instance.AddPathReference(unit))
			{
				while (!Monitor.TryEnter(this.m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
				{
				}
				try
				{
					if (skipQueue)
					{
						if (this.m_queueLast == 0u)
						{
							this.m_queueLast = unit;
						}
						else
						{
							this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_nextPathUnit = this.m_queueFirst;
						}
						this.m_queueFirst = unit;
					}
					else
					{
						if (this.m_queueLast == 0u)
						{
							this.m_queueFirst = unit;
						}
						else
						{
							this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_queueLast)].m_nextPathUnit = unit;
						}
						this.m_queueLast = unit;
					}

					m_pathVehicleType[unit] = vehicleType;

					PathUnit[] expr_BD_cp_0 = this.m_pathUnits.m_buffer;
					UIntPtr expr_BD_cp_1 = (UIntPtr)unit;
					expr_BD_cp_0[(int)expr_BD_cp_1].m_pathFindFlags = (byte)(expr_BD_cp_0[(int)expr_BD_cp_1].m_pathFindFlags | 1);
					Facade.m_queuedPathFindCount++;
					Monitor.Pulse(this.m_queueLock);
				}
				finally
				{
					Monitor.Exit(this.m_queueLock);
				}
				return true;
			}
			return false;
		}

        public void WaitForAllPaths()
        {
            while (!Monitor.TryEnter(this.m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
            {
            }
            try
            {
                while ((this.m_queueFirst != 0u || this.m_calculating != 0u) && !this.m_terminated)
                {
                    Monitor.Wait(this.m_queueLock);
                }
            }
            finally
            {
                Monitor.Exit(this.m_queueLock);
            }
        }

        private void PathFindImplementation(uint unit, ref PathUnit data)
        {
            NetManager instance = Singleton<NetManager>.instance;
            this.m_laneTypes = (NetInfo.LaneType)this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_laneTypes;
            this.m_vehicleTypes = (VehicleInfo.VehicleType)this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_vehicleTypes;
            this.m_maxLength = this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_length;
            this.m_pathFindIndex = (this.m_pathFindIndex + 1u & 32767u);
            this.m_pathRandomizer = new Randomizer(unit);
            this.m_isHeavyVehicle = ((this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_simulationFlags & 16) != 0);
            this.m_ignoreBlocked = ((this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_simulationFlags & 32) != 0);
            this.m_stablePath = ((this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_simulationFlags & 64) != 0);
            this.m_transportVehicle = ((byte)(this.m_laneTypes & NetInfo.LaneType.TransportVehicle) != 0);
            if ((byte)(this.m_laneTypes & NetInfo.LaneType.Vehicle) != 0)
            {
                this.m_laneTypes |= NetInfo.LaneType.TransportVehicle;
            }

            if (!m_pathVehicleType.TryGetValue(unit, out m_vehicleTypeExtended))
            {
                this.m_vehicleTypeExtended = ExtendedVehicleType.Unknown;
            }

            //Logger.LogInfo("PathFindImplementation of " + m_vehicleTypeExtended);

            this.m_priorityVehicle = (this.m_vehicleTypeExtended & (ExtendedVehicleType.Bus | ExtendedVehicleType.EmergencyVehicles)) != ExtendedVehicleType.None;

            int num = (int)(this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_positionCount & 15);
            int num2 = this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_positionCount >> 4;
            BufferItem bufferItem;
            if (data.m_position00.m_segment != 0 && num >= 1)
            {
                this.m_startLaneA = PathManager.GetLaneID(data.m_position00);
                this.m_startOffsetA = data.m_position00.m_offset;
                bufferItem.m_laneID = this.m_startLaneA;
                bufferItem.m_position = data.m_position00;
                this.GetLaneDirection(data.m_position00, out bufferItem.m_direction, out bufferItem.m_lanesUsed);
                bufferItem.m_comparisonValue = 0f;
            }
            else
            {
                this.m_startLaneA = 0u;
                this.m_startOffsetA = 0;
                bufferItem = default(BufferItem);
            }
            BufferItem bufferItem2;
            if (data.m_position02.m_segment != 0 && num >= 3)
            {
                this.m_startLaneB = PathManager.GetLaneID(data.m_position02);
                this.m_startOffsetB = data.m_position02.m_offset;
                bufferItem2.m_laneID = this.m_startLaneB;
                bufferItem2.m_position = data.m_position02;
                this.GetLaneDirection(data.m_position02, out bufferItem2.m_direction, out bufferItem2.m_lanesUsed);
                bufferItem2.m_comparisonValue = 0f;
            }
            else
            {
                this.m_startLaneB = 0u;
                this.m_startOffsetB = 0;
                bufferItem2 = default(BufferItem);
            }
            BufferItem bufferItem3;
            if (data.m_position01.m_segment != 0 && num >= 2)
            {
                this.m_endLaneA = PathManager.GetLaneID(data.m_position01);
                bufferItem3.m_laneID = this.m_endLaneA;
                bufferItem3.m_position = data.m_position01;
                this.GetLaneDirection(data.m_position01, out bufferItem3.m_direction, out bufferItem3.m_lanesUsed);
                bufferItem3.m_methodDistance = 0f;
                bufferItem3.m_comparisonValue = 0f;
            }
            else
            {
                this.m_endLaneA = 0u;
                bufferItem3 = default(BufferItem);
            }
            BufferItem bufferItem4;
            if (data.m_position03.m_segment != 0 && num >= 4)
            {
                this.m_endLaneB = PathManager.GetLaneID(data.m_position03);
                bufferItem4.m_laneID = this.m_endLaneB;
                bufferItem4.m_position = data.m_position03;
                this.GetLaneDirection(data.m_position03, out bufferItem4.m_direction, out bufferItem4.m_lanesUsed);
                bufferItem4.m_methodDistance = 0f;
                bufferItem4.m_comparisonValue = 0f;
            }
            else
            {
                this.m_endLaneB = 0u;
                bufferItem4 = default(BufferItem);
            }
            if (data.m_position11.m_segment != 0 && num2 >= 1)
            {
                this.m_vehicleLane = PathManager.GetLaneID(data.m_position11);
                this.m_vehicleOffset = data.m_position11.m_offset;
            }
            else
            {
                this.m_vehicleLane = 0u;
                this.m_vehicleOffset = 0;
            }
            BufferItem bufferItem5 = default(BufferItem);
            byte b = 0;
            this.m_bufferMinPos = 0;
            this.m_bufferMaxPos = -1;
            if (this.m_pathFindIndex == 0u)
            {
                uint num3 = 4294901760u;
                for (int i = 0; i < 262144; i++)
                {
                    this.m_laneLocation[i] = num3;
                }
            }
            for (int j = 0; j < 1024; j++)
            {
                this.m_bufferMin[j] = 0;
                this.m_bufferMax[j] = -1;
            }
            if (bufferItem3.m_position.m_segment != 0)
            {
                this.m_bufferMax[0]++;
                this.m_buffer[++this.m_bufferMaxPos] = bufferItem3;
            }
            if (bufferItem4.m_position.m_segment != 0)
            {
                this.m_bufferMax[0]++;
                this.m_buffer[++this.m_bufferMaxPos] = bufferItem4;
            }
            bool flag = false;
            while (this.m_bufferMinPos <= this.m_bufferMaxPos)
            {
                int num4 = this.m_bufferMin[this.m_bufferMinPos];
                int num5 = this.m_bufferMax[this.m_bufferMinPos];
                if (num4 > num5)
                {
                    this.m_bufferMinPos++;
                }
                else
                {
                    this.m_bufferMin[this.m_bufferMinPos] = num4 + 1;
                    BufferItem bufferItem6 = this.m_buffer[(this.m_bufferMinPos << 6) + num4];
                    if (bufferItem6.m_position.m_segment == bufferItem.m_position.m_segment && bufferItem6.m_position.m_lane == bufferItem.m_position.m_lane)
                    {
                        if ((byte)(bufferItem6.m_direction & NetInfo.Direction.Forward) != 0 && bufferItem6.m_position.m_offset >= this.m_startOffsetA)
                        {
                            bufferItem5 = bufferItem6;
                            b = this.m_startOffsetA;
                            flag = true;
                            break;
                        }
                        if ((byte)(bufferItem6.m_direction & NetInfo.Direction.Backward) != 0 && bufferItem6.m_position.m_offset <= this.m_startOffsetA)
                        {
                            bufferItem5 = bufferItem6;
                            b = this.m_startOffsetA;
                            flag = true;
                            break;
                        }
                    }
                    if (bufferItem6.m_position.m_segment == bufferItem2.m_position.m_segment && bufferItem6.m_position.m_lane == bufferItem2.m_position.m_lane)
                    {
                        if ((byte)(bufferItem6.m_direction & NetInfo.Direction.Forward) != 0 && bufferItem6.m_position.m_offset >= this.m_startOffsetB)
                        {
                            bufferItem5 = bufferItem6;
                            b = this.m_startOffsetB;
                            flag = true;
                            break;
                        }
                        if ((byte)(bufferItem6.m_direction & NetInfo.Direction.Backward) != 0 && bufferItem6.m_position.m_offset <= this.m_startOffsetB)
                        {
                            bufferItem5 = bufferItem6;
                            b = this.m_startOffsetB;
                            flag = true;
                            break;
                        }
                    }
                    if ((byte)(bufferItem6.m_direction & NetInfo.Direction.Forward) != 0)
                    {
                        ushort startNode = instance.m_segments.m_buffer[(int)bufferItem6.m_position.m_segment].m_startNode;
                        this.ProcessItem(bufferItem6, startNode, ref instance.m_nodes.m_buffer[(int)startNode], 0, false);
                    }
                    if ((byte)(bufferItem6.m_direction & NetInfo.Direction.Backward) != 0)
                    {
                        ushort endNode = instance.m_segments.m_buffer[(int)bufferItem6.m_position.m_segment].m_endNode;
                        this.ProcessItem(bufferItem6, endNode, ref instance.m_nodes.m_buffer[(int)endNode], 255, false);
                    }
                    int num6 = 0;
                    ushort num7 = instance.m_lanes.m_buffer[(int)((UIntPtr)bufferItem6.m_laneID)].m_nodes;
                    if (num7 != 0)
                    {
                        ushort startNode2 = instance.m_segments.m_buffer[(int)bufferItem6.m_position.m_segment].m_startNode;
                        ushort endNode2 = instance.m_segments.m_buffer[(int)bufferItem6.m_position.m_segment].m_endNode;
                        bool flag2 = ((instance.m_nodes.m_buffer[(int)startNode2].m_flags | instance.m_nodes.m_buffer[(int)endNode2].m_flags) & NetNode.Flags.Disabled) != NetNode.Flags.None;
                        while (num7 != 0)
                        {
                            NetInfo.Direction direction = NetInfo.Direction.None;
                            byte laneOffset = instance.m_nodes.m_buffer[(int)num7].m_laneOffset;
                            if (laneOffset <= bufferItem6.m_position.m_offset)
                            {
                                direction |= NetInfo.Direction.Forward;
                            }
                            if (laneOffset >= bufferItem6.m_position.m_offset)
                            {
                                direction |= NetInfo.Direction.Backward;
                            }
                            if ((byte)(bufferItem6.m_direction & direction) != 0 && (!flag2 || (instance.m_nodes.m_buffer[(int)num7].m_flags & NetNode.Flags.Disabled) != NetNode.Flags.None))
                            {
                                this.ProcessItem(bufferItem6, num7, ref instance.m_nodes.m_buffer[(int)num7], laneOffset, true);
                            }
                            num7 = instance.m_nodes.m_buffer[(int)num7].m_nextLaneNode;
                            if (++num6 == 32768)
                            {
                                break;
                            }
                        }
                    }
                }
            }
            if (!flag)
            {
                PathUnit[] expr_909_cp_0 = this.m_pathUnits.m_buffer;
                UIntPtr expr_909_cp_1 = (UIntPtr)unit;
                expr_909_cp_0[(int)expr_909_cp_1].m_pathFindFlags = (byte)(expr_909_cp_0[(int)expr_909_cp_1].m_pathFindFlags | 8);
                return;
            }
            float num8 = bufferItem5.m_comparisonValue * this.m_maxLength;
            this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_length = num8;
            uint num9 = unit;
            int num10 = 0;
            int num11 = 0;
            PathUnit.Position position = bufferItem5.m_position;
            if ((position.m_segment != bufferItem3.m_position.m_segment || position.m_lane != bufferItem3.m_position.m_lane || position.m_offset != bufferItem3.m_position.m_offset) && (position.m_segment != bufferItem4.m_position.m_segment || position.m_lane != bufferItem4.m_position.m_lane || position.m_offset != bufferItem4.m_position.m_offset))
            {
                if (b != position.m_offset)
                {
                    PathUnit.Position position2 = position;
                    position2.m_offset = b;
                    this.m_pathUnits.m_buffer[(int)((UIntPtr)num9)].SetPosition(num10++, position2);
                }
                this.m_pathUnits.m_buffer[(int)((UIntPtr)num9)].SetPosition(num10++, position);
                position = this.m_laneTarget[(int)((UIntPtr)bufferItem5.m_laneID)];
            }
            for (int k = 0; k < 262144; k++)
            {
                this.m_pathUnits.m_buffer[(int)((UIntPtr)num9)].SetPosition(num10++, position);
                if ((position.m_segment == bufferItem3.m_position.m_segment && position.m_lane == bufferItem3.m_position.m_lane && position.m_offset == bufferItem3.m_position.m_offset) || (position.m_segment == bufferItem4.m_position.m_segment && position.m_lane == bufferItem4.m_position.m_lane && position.m_offset == bufferItem4.m_position.m_offset))
                {
                    this.m_pathUnits.m_buffer[(int)((UIntPtr)num9)].m_positionCount = (byte)num10;
                    num11 += num10;
                    if (num11 != 0)
                    {
                        num9 = this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_nextPathUnit;
                        num10 = (int)this.m_pathUnits.m_buffer[(int)((UIntPtr)unit)].m_positionCount;
                        int num12 = 0;
                        while (num9 != 0u)
                        {
                            this.m_pathUnits.m_buffer[(int)((UIntPtr)num9)].m_length = num8 * (float)(num11 - num10) / (float)num11;
                            num10 += (int)this.m_pathUnits.m_buffer[(int)((UIntPtr)num9)].m_positionCount;
                            num9 = this.m_pathUnits.m_buffer[(int)((UIntPtr)num9)].m_nextPathUnit;
                            if (++num12 >= 262144)
                            {
                                CODebugBase<LogChannel>.Error(LogChannel.Core, "Invalid list detected!\n" + Environment.StackTrace);
                                break;
                            }
                        }
                    }
                    PathUnit[] expr_C16_cp_0 = this.m_pathUnits.m_buffer;
                    UIntPtr expr_C16_cp_1 = (UIntPtr)unit;
                    expr_C16_cp_0[(int)expr_C16_cp_1].m_pathFindFlags = (byte)(expr_C16_cp_0[(int)expr_C16_cp_1].m_pathFindFlags | 4);
                    return;
                }
                if (num10 == 12)
                {
                    while (!Monitor.TryEnter(this.m_bufferLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
                    {
                    }
                    uint num13;
                    try
                    {
                        if (!this.m_pathUnits.CreateItem(out num13, ref this.m_pathRandomizer))
                        {
                            PathUnit[] expr_D15_cp_0 = this.m_pathUnits.m_buffer;
                            UIntPtr expr_D15_cp_1 = (UIntPtr)unit;
                            expr_D15_cp_0[(int)expr_D15_cp_1].m_pathFindFlags = (byte)(expr_D15_cp_0[(int)expr_D15_cp_1].m_pathFindFlags | 8);
                            return;
                        }
                        this.m_pathUnits.m_buffer[(int)((UIntPtr)num13)] = this.m_pathUnits.m_buffer[(int)((UIntPtr)num9)];
                        this.m_pathUnits.m_buffer[(int)((UIntPtr)num13)].m_referenceCount = 1;
                        this.m_pathUnits.m_buffer[(int)((UIntPtr)num13)].m_pathFindFlags = 4;
                        this.m_pathUnits.m_buffer[(int)((UIntPtr)num9)].m_nextPathUnit = num13;
                        this.m_pathUnits.m_buffer[(int)((UIntPtr)num9)].m_positionCount = (byte)num10;
                        num11 += num10;
                        Singleton<PathManager>.instance.m_pathUnitCount = (int)(this.m_pathUnits.ItemCount() - 1u);
                    }
                    finally
                    {
                        Monitor.Exit(this.m_bufferLock);
                    }
                    num9 = num13;
                    num10 = 0;
                }
                uint laneID = PathManager.GetLaneID(position);
                position = this.m_laneTarget[(int)((UIntPtr)laneID)];
            }
            PathUnit[] expr_D99_cp_0 = this.m_pathUnits.m_buffer;
            UIntPtr expr_D99_cp_1 = (UIntPtr)unit;
            expr_D99_cp_0[(int)expr_D99_cp_1].m_pathFindFlags = (byte)(expr_D99_cp_0[(int)expr_D99_cp_1].m_pathFindFlags | 8);
        }

        private void ProcessItem(BufferItem item, ushort nodeID, ref NetNode node, byte connectOffset, bool isMiddle)
        {
            NetManager instance = Singleton<NetManager>.instance;
            bool flag = false;
            bool flag2 = false;
            bool flag3 = false;
            int num = 0;
            NetInfo info = instance.m_segments.m_buffer[(int)item.m_position.m_segment].Info;
            if ((int)item.m_position.m_lane < info.m_lanes.Length)
            {
                NetInfo.Lane lane = info.m_lanes[(int)item.m_position.m_lane].ShallowClone();
                flag = (lane.m_laneType == NetInfo.LaneType.Pedestrian);
                flag2 = (lane.m_laneType == NetInfo.LaneType.Vehicle && (lane.m_vehicleType & this.m_vehicleTypes) == VehicleInfo.VehicleType.Bicycle);
                flag3 = lane.m_centerPlatform;
                if ((byte)(lane.m_finalDirection & NetInfo.Direction.Forward) != 0)
                {
                    num = lane.m_similarLaneIndex;
                }
                else
                {
                    num = lane.m_similarLaneCount - lane.m_similarLaneIndex - 1;
                }
            }
            if (isMiddle)
            {
                for (int i = 0; i < 8; i++)
                {
                    ushort segment = node.GetSegment(i);
                    if (segment != 0)
                    {
                        this.ProcessItem(item, nodeID, segment, ref instance.m_segments.m_buffer[(int)segment], ref num, connectOffset, !flag, flag);
                    }
                }
            }
            else if (flag)
            {
                ushort segment2 = item.m_position.m_segment;
                int lane2 = (int)item.m_position.m_lane;
                if (node.Info.m_class.m_service != ItemClass.Service.Beautification)
                {
                    bool flag4 = (node.m_flags & (NetNode.Flags.End | NetNode.Flags.Bend | NetNode.Flags.Junction)) != NetNode.Flags.None;
                    bool flag5 = flag3 && (node.m_flags & (NetNode.Flags.End | NetNode.Flags.Junction)) == NetNode.Flags.None;
                    ushort num2 = segment2;
                    ushort num3 = segment2;
                    int laneIndex;
                    int laneIndex2;
                    uint num4;
                    uint num5;
                    instance.m_segments.m_buffer[(int)segment2].GetLeftAndRightLanes(nodeID, NetInfo.LaneType.Pedestrian, VehicleInfo.VehicleType.None, lane2, flag5, out laneIndex, out laneIndex2, out num4, out num5);
                    if (num4 == 0u || num5 == 0u)
                    {
                        ushort leftSegment;
                        ushort rightSegment;
                        instance.m_segments.m_buffer[(int)segment2].GetLeftAndRightSegments(nodeID, out leftSegment, out rightSegment);
                        int num6 = 0;
                        while (leftSegment != 0 && leftSegment != segment2 && num4 == 0u)
                        {
                            int num7;
                            int num8;
                            uint num9;
                            uint num10;
                            instance.m_segments.m_buffer[(int)leftSegment].GetLeftAndRightLanes(nodeID, NetInfo.LaneType.Pedestrian, VehicleInfo.VehicleType.None, -1, flag5, out num7, out num8, out num9, out num10);
                            if (num10 != 0u)
                            {
                                num2 = leftSegment;
                                laneIndex = num8;
                                num4 = num10;
                            }
                            else
                            {
                                leftSegment = instance.m_segments.m_buffer[(int)leftSegment].GetLeftSegment(nodeID);
                            }
                            if (++num6 == 8)
                            {
                                break;
                            }
                        }
                        num6 = 0;
                        while (rightSegment != 0 && rightSegment != segment2 && num5 == 0u)
                        {
                            int num11;
                            int num12;
                            uint num13;
                            uint num14;
                            instance.m_segments.m_buffer[(int)rightSegment].GetLeftAndRightLanes(nodeID, NetInfo.LaneType.Pedestrian, VehicleInfo.VehicleType.None, -1, flag5, out num11, out num12, out num13, out num14);
                            if (num13 != 0u)
                            {
                                num3 = rightSegment;
                                laneIndex2 = num11;
                                num5 = num13;
                            }
                            else
                            {
                                rightSegment = instance.m_segments.m_buffer[(int)rightSegment].GetRightSegment(nodeID);
                            }
                            if (++num6 == 8)
                            {
                                break;
                            }
                        }
                    }
                    if (num4 != 0u && (num2 != segment2 || flag4 || flag5))
                    {
                        this.ProcessItem(item, nodeID, num2, ref instance.m_segments.m_buffer[(int)num2], connectOffset, laneIndex, num4);
                    }
                    if (num5 != 0u && num5 != num4 && (num3 != segment2 || flag4 || flag5))
                    {
                        this.ProcessItem(item, nodeID, num3, ref instance.m_segments.m_buffer[(int)num3], connectOffset, laneIndex2, num5);
                    }
                    int laneIndex3;
                    uint lane3;
                    if ((this.m_vehicleTypes & VehicleInfo.VehicleType.Bicycle) != VehicleInfo.VehicleType.None && instance.m_segments.m_buffer[(int)segment2].GetClosestLane((int)item.m_position.m_lane, NetInfo.LaneType.Vehicle, VehicleInfo.VehicleType.Bicycle, out laneIndex3, out lane3))
                    {
                        this.ProcessItem(item, nodeID, segment2, ref instance.m_segments.m_buffer[(int)segment2], connectOffset, laneIndex3, lane3);
                    }
                }
                else
                {
                    for (int j = 0; j < 8; j++)
                    {
                        ushort segment3 = node.GetSegment(j);
                        if (segment3 != 0 && segment3 != segment2)
                        {
                            this.ProcessItem(item, nodeID, segment3, ref instance.m_segments.m_buffer[(int)segment3], ref num, connectOffset, false, true);
                        }
                    }
                }
                NetInfo.LaneType laneType = this.m_laneTypes & ~NetInfo.LaneType.Pedestrian;
                VehicleInfo.VehicleType vehicleType = this.m_vehicleTypes & ~VehicleInfo.VehicleType.Bicycle;
                if ((byte)(item.m_lanesUsed & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0)
                {
                    laneType &= ~(NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle);
                }
                int num15;
                uint lane4;
                if (laneType != NetInfo.LaneType.None && vehicleType != VehicleInfo.VehicleType.None && instance.m_segments.m_buffer[(int)segment2].GetClosestLane(lane2, laneType, vehicleType, out num15, out lane4))
                {
                    NetInfo.Lane lane5 = info.m_lanes[num15].ShallowClone();
                    byte connectOffset2;
                    if ((instance.m_segments.m_buffer[(int)segment2].m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None == ((byte)(lane5.m_finalDirection & NetInfo.Direction.Backward) != 0))
                    {
                        connectOffset2 = 1;
                    }
                    else
                    {
                        connectOffset2 = 254;
                    }
                    this.ProcessItem(item, nodeID, segment2, ref instance.m_segments.m_buffer[(int)segment2], connectOffset2, num15, lane4);
                }
            }
            else
            {
                bool flag6 = (node.m_flags & (NetNode.Flags.End | NetNode.Flags.OneWayOut)) != NetNode.Flags.None;
                bool flag7 = (byte)(this.m_laneTypes & NetInfo.LaneType.Pedestrian) != 0;
                bool enablePedestrian = false;
                byte connectOffset3 = 0;
                if (flag7)
                {
                    if (flag2)
                    {
                        connectOffset3 = connectOffset;
                        enablePedestrian = (node.Info.m_class.m_service == ItemClass.Service.Beautification);
                    }
                    else if (this.m_vehicleLane != 0u)
                    {
                        if (this.m_vehicleLane != item.m_laneID)
                        {
                            flag7 = false;
                        }
                        else
                        {
                            connectOffset3 = this.m_vehicleOffset;
                        }
                    }
                    else if (this.m_stablePath)
                    {
                        connectOffset3 = 128;
                    }
                    else
                    {
                        connectOffset3 = (byte)this.m_pathRandomizer.UInt32(1u, 254u);
                    }
                }
                ushort num16 = instance.m_segments.m_buffer[(int)item.m_position.m_segment].GetRightSegment(nodeID);
                for (int k = 0; k < 8; k++)
                {
                    if (num16 == 0 || num16 == item.m_position.m_segment)
                    {
                        break;
                    }
                    if (this.ProcessItem(item, nodeID, num16, ref instance.m_segments.m_buffer[(int)num16], ref num, connectOffset, true, enablePedestrian))
                    {
                        flag6 = true;
                    }
                    num16 = instance.m_segments.m_buffer[(int)num16].GetRightSegment(nodeID);
                }
                if (flag6 && (this.m_vehicleTypes & VehicleInfo.VehicleType.Tram) == VehicleInfo.VehicleType.None)
                {
                    num16 = item.m_position.m_segment;
                    this.ProcessItem(item, nodeID, num16, ref instance.m_segments.m_buffer[(int)num16], ref num, connectOffset, true, false);
                }
                if (flag7)
                {
                    num16 = item.m_position.m_segment;
                    int laneIndex4;
                    uint lane6;
                    if (instance.m_segments.m_buffer[(int)num16].GetClosestLane((int)item.m_position.m_lane, NetInfo.LaneType.Pedestrian, this.m_vehicleTypes, out laneIndex4, out lane6))
                    {
                        this.ProcessItem(item, nodeID, num16, ref instance.m_segments.m_buffer[(int)num16], connectOffset3, laneIndex4, lane6);
                    }
                }
            }
            if (node.m_lane != 0u)
            {
                bool targetDisabled = (node.m_flags & NetNode.Flags.Disabled) != NetNode.Flags.None;
                ushort segment4 = instance.m_lanes.m_buffer[(int)((UIntPtr)node.m_lane)].m_segment;
                if (segment4 != 0 && segment4 != item.m_position.m_segment)
                {
                    this.ProcessItem(item, nodeID, targetDisabled, segment4, ref instance.m_segments.m_buffer[(int)segment4], node.m_lane, node.m_laneOffset, connectOffset);
                }
            }
        }

        private float CalculateLaneSpeed(byte startOffset, byte endOffset, ref NetSegment segment, NetInfo.Lane laneInfo, uint laneId)
        {
            float speedLimit = LaneManager.GetLaneSpeed(laneId, laneInfo);
            //float speedLimit = laneInfo.m_speedLimit;

            NetInfo.Direction direction = ((segment.m_flags & NetSegment.Flags.Invert) == NetSegment.Flags.None) ? laneInfo.m_finalDirection : NetInfo.InvertDirection(laneInfo.m_finalDirection);
            if ((byte)(direction & NetInfo.Direction.Avoid) == 0)
            {
                //return laneInfo.m_speedLimit;
                return speedLimit;
            }
            if (endOffset > startOffset && direction == NetInfo.Direction.AvoidForward)
            {
                //return laneInfo.m_speedLimit * 0.1f;
                return speedLimit * 0.1f;
            }
            if (endOffset < startOffset && direction == NetInfo.Direction.AvoidBackward)
            {
                //return laneInfo.m_speedLimit * 0.1f;
                return speedLimit * 0.1f;
            }
            //return laneInfo.m_speedLimit * 0.2f;
            return speedLimit * 0.2f;
        }

        private void ProcessItem(BufferItem item, ushort targetNode, bool targetDisabled, ushort segmentID, ref NetSegment segment, uint lane, byte offset, byte connectOffset)
        {
            if ((segment.m_flags & (NetSegment.Flags.PathFailed | NetSegment.Flags.Flooded)) != NetSegment.Flags.None)
            {
                return;
            }
            NetManager instance = Singleton<NetManager>.instance;
            if (targetDisabled && ((instance.m_nodes.m_buffer[(int)segment.m_startNode].m_flags | instance.m_nodes.m_buffer[(int)segment.m_endNode].m_flags) & NetNode.Flags.Disabled) == NetNode.Flags.None)
            {
                return;
            }
            NetInfo info = segment.Info;
            NetInfo info2 = instance.m_segments.m_buffer[(int)item.m_position.m_segment].Info;
            int num = info.m_lanes.Length;
            uint num2 = segment.m_lanes;
            float num3 = 1f;
            float num4 = 1f;
            NetInfo.LaneType laneType = NetInfo.LaneType.None;
            if ((int)item.m_position.m_lane < info2.m_lanes.Length)
            {
                uint l = instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_lanes;
                for (int n = 0; l != 0 && n < (int)item.m_position.m_lane; ++n)
                    l = instance.m_lanes.m_buffer[l].m_nextLane;

                NetInfo.Lane lane2 = info2.m_lanes[(int)item.m_position.m_lane];
                num3 = LaneManager.GetLaneSpeed(l, lane2);
                laneType = lane2.m_laneType;
                if ((byte)(laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0)
                {
                    laneType = (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle);
                }
                num4 = this.CalculateLaneSpeed(connectOffset, item.m_position.m_offset, ref instance.m_segments.m_buffer[(int)item.m_position.m_segment], lane2, l);
            }
            float averageLength = instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_averageLength;
            float num5 = (float)Mathf.Abs((int)(connectOffset - item.m_position.m_offset)) * 0.003921569f * averageLength;
            float num6 = item.m_methodDistance + num5;
            float num7 = item.m_comparisonValue + num5 / (num4 * this.m_maxLength);
            Vector3 b = instance.m_lanes.m_buffer[(int)((UIntPtr)item.m_laneID)].CalculatePosition((float)connectOffset * 0.003921569f);
            int num8 = 0;
            while (num8 < num && num2 != 0u)
            {
                if (lane == num2)
                {
                    NetInfo.Lane lane3 = info.m_lanes[num8].ShallowClone();
                    if (lane3.CheckType(this.m_laneTypes, this.m_vehicleTypes))
                    {
                        Vector3 a = instance.m_lanes.m_buffer[(int)((UIntPtr)lane)].CalculatePosition((float)offset * 0.003921569f);
                        float num9 = Vector3.Distance(a, b);
                        BufferItem item2;
                        item2.m_position.m_segment = segmentID;
                        item2.m_position.m_lane = (byte)num8;
                        item2.m_position.m_offset = offset;
                        if ((byte)(lane3.m_laneType & laneType) == 0)
                        {
                            item2.m_methodDistance = 0f;
                        }
                        else
                        {
                            item2.m_methodDistance = num6 + num9;
                        }
                        if (lane3.m_laneType != NetInfo.LaneType.Pedestrian || item2.m_methodDistance < 1000f)
                        {
                            item2.m_comparisonValue = num7 + num9 / ((num3 + LaneManager.GetLaneSpeed(lane, lane3)) * 0.5f * this.m_maxLength);
                            if ((segment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None)
                            {
                                item2.m_direction = NetInfo.InvertDirection(lane3.m_finalDirection);
                            }
                            else
                            {
                                item2.m_direction = lane3.m_finalDirection;
                            }
                            if (lane == this.m_startLaneA)
                            {
                                if (((byte)(item2.m_direction & NetInfo.Direction.Forward) == 0 || item2.m_position.m_offset < this.m_startOffsetA) && ((byte)(item2.m_direction & NetInfo.Direction.Backward) == 0 || item2.m_position.m_offset > this.m_startOffsetA))
                                {
                                    return;
                                }
                                float num10 = this.CalculateLaneSpeed(this.m_startOffsetA, item2.m_position.m_offset, ref segment, lane3, lane);
                                float num11 = (float)Mathf.Abs((int)(item2.m_position.m_offset - this.m_startOffsetA)) * 0.003921569f;
                                item2.m_comparisonValue += num11 * segment.m_averageLength / (num10 * this.m_maxLength);
                            }
                            if (lane == this.m_startLaneB)
                            {
                                if (((byte)(item2.m_direction & NetInfo.Direction.Forward) == 0 || item2.m_position.m_offset < this.m_startOffsetB) && ((byte)(item2.m_direction & NetInfo.Direction.Backward) == 0 || item2.m_position.m_offset > this.m_startOffsetB))
                                {
                                    return;
                                }
                                float num12 = this.CalculateLaneSpeed(this.m_startOffsetB, item2.m_position.m_offset, ref segment, lane3, lane);
                                float num13 = (float)Mathf.Abs((int)(item2.m_position.m_offset - this.m_startOffsetB)) * 0.003921569f;
                                item2.m_comparisonValue += num13 * segment.m_averageLength / (num12 * this.m_maxLength);
                            }
                            item2.m_laneID = lane;
                            item2.m_lanesUsed = (item.m_lanesUsed | lane3.m_laneType);
                            this.AddBufferItem(item2, item.m_position);
                        }
                    }
                    return;
                }
                num2 = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_nextLane;
                num8++;
            }
        }

        private bool ProcessItem(BufferItem item, ushort targetNode, ushort segmentID, ref NetSegment segment, ref int currentTargetIndex, byte connectOffset, bool enableVehicle, bool enablePedestrian)
        {
            if ((segment.m_flags & (NetSegment.Flags.PathFailed | NetSegment.Flags.Flooded)) != NetSegment.Flags.None)
            {
                return false;
            }

            bool result = false;
            NetManager instance = Singleton<NetManager>.instance;
            NetInfo info = segment.Info;
            NetInfo info2 = instance.m_segments.m_buffer[(int)item.m_position.m_segment].Info;
            int num = info.m_lanes.Length;
            uint num2 = segment.m_lanes;
            NetInfo.Direction direction = (targetNode != segment.m_startNode) ? NetInfo.Direction.Forward : NetInfo.Direction.Backward;
            NetInfo.Direction direction2 = ((segment.m_flags & NetSegment.Flags.Invert) == NetSegment.Flags.None) ? direction : NetInfo.InvertDirection(direction);
            float num3 = 0.01f - Mathf.Min(info.m_maxTurnAngleCos, info2.m_maxTurnAngleCos);
            if (num3 < 1f)
            {
                Vector3 vector;
                if (targetNode == instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_startNode)
                {
                    vector = instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_startDirection;
                }
                else
                {
                    vector = instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_endDirection;
                }
                Vector3 vector2;
                if ((byte)(direction & NetInfo.Direction.Forward) != 0)
                {
                    vector2 = segment.m_endDirection;
                }
                else
                {
                    vector2 = segment.m_startDirection;
                }
                float num4 = vector.x * vector2.x + vector.z * vector2.z;
                if (num4 >= num3)
                {
                    return result;
                }
            }
            float num5 = 1f;
            float num6 = 1f;
            NetInfo.LaneType laneType = NetInfo.LaneType.None;
            VehicleInfo.VehicleType vehicleType = VehicleInfo.VehicleType.None;
            if ((int)item.m_position.m_lane < info2.m_lanes.Length)
            {
				uint l = instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_lanes;
				for (int n = 0; l != 0 && n < (int)item.m_position.m_lane; ++n)
					l = instance.m_lanes.m_buffer[l].m_nextLane;

                NetInfo.Lane lane = info2.m_lanes[(int)item.m_position.m_lane];
                laneType = lane.m_laneType;
                vehicleType = lane.m_vehicleType;
				num5 = LaneManager.GetLaneSpeed(l, lane);
                num6 = this.CalculateLaneSpeed(connectOffset, item.m_position.m_offset, ref instance.m_segments.m_buffer[(int)item.m_position.m_segment], lane, l);
            }
            float num7 = instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_averageLength;
            if (!this.m_stablePath)
            {
                Randomizer randomizer = new Randomizer(this.m_pathFindIndex << 16 | (uint)item.m_position.m_segment);
                num7 *= (float)(randomizer.Int32(900, 1000 + (int)(instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_trafficDensity * 10)) + this.m_pathRandomizer.Int32(20u)) * 0.001f;
            }
            if (this.m_isHeavyVehicle && (instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_flags & NetSegment.Flags.HeavyBan) != NetSegment.Flags.None)
            {
                num7 *= 10f;
            }
            else if (laneType == NetInfo.LaneType.Vehicle && (vehicleType & this.m_vehicleTypes) == VehicleInfo.VehicleType.Car && (instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_flags & NetSegment.Flags.CarBan) != NetSegment.Flags.None)
            {
                num7 *= 5f;
            }
            /* --------- TAM - Fix -------- */
            //if (this.m_isTransportVehicle && (laneType & NetInfo.LaneType.TransportVehicle) != 0)
            //{
            //    num7 *= 0.95f;
            //}
            if ((laneType & NetInfo.LaneType.TransportVehicle) != 0)
            {
                if (this.m_priorityVehicle || this.m_transportVehicle)
                {
                    num7 *= 0.90f;
                }
                else
                {
                    num7 *= 1.5f;
                }
            }
            /* ------------ */
            if ((byte)(laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0)
            {
                laneType = (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle);
            }
            float num8 = (float)Mathf.Abs((int)(connectOffset - item.m_position.m_offset)) * 0.003921569f * num7;
            float num9 = item.m_methodDistance + num8;
            float num10 = item.m_comparisonValue + num8 / (num6 * this.m_maxLength);
            Vector3 b = instance.m_lanes.m_buffer[(int)((UIntPtr)item.m_laneID)].CalculatePosition((float)connectOffset * 0.003921569f);
            int num11 = currentTargetIndex;
            bool flag = (instance.m_nodes.m_buffer[(int)targetNode].m_flags & NetNode.Flags.Transition) != NetNode.Flags.None;
            NetInfo.LaneType laneType2 = this.m_laneTypes;
            VehicleInfo.VehicleType vehicleType2 = this.m_vehicleTypes;
            if (!enableVehicle)
            {
                vehicleType2 &= VehicleInfo.VehicleType.Bicycle;
                if (vehicleType2 == VehicleInfo.VehicleType.None)
                {
                    laneType2 &= ~(NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle);
                }
            }
            if (!enablePedestrian)
            {
                laneType2 &= ~NetInfo.LaneType.Pedestrian;
            }
            int num12 = 0;
            while (num12 < num && num2 != 0u)
            {
                NetInfo.Lane lane2 = info.m_lanes[num12].ShallowClone();

                if ((byte)(lane2.m_finalDirection & direction2) != 0 &&
                // TAM Restrictions
                    lane2.CheckLaneConnection(this.m_vehicleTypeExtended, num2, item.m_laneID) &&
                    lane2.CanUseLane(this.m_vehicleTypeExtended, num2) &&
                    lane2.CanUseLane(this.m_vehicleTypeExtended, item.m_laneID))
                // TAM Restrictions
                {
                    if (lane2.CheckType(laneType2, vehicleType2) && (segmentID != item.m_position.m_segment || num12 != (int)item.m_position.m_lane) && (byte)(lane2.m_finalDirection & direction2) != 0)
                    {
                        Vector3 a;
                        if ((byte)(direction & NetInfo.Direction.Forward) != 0)
                        {
                            a = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_bezier.d;
                        }
                        else
                        {
                            a = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_bezier.a;
                        }
                        float num13 = Vector3.Distance(a, b);
                        if (flag)
                        {
                            num13 *= 2f;
                        }
                        float num14 = num13 / ((num5 + LaneManager.GetLaneSpeed(num2, lane2)) * 0.5f * this.m_maxLength);
                        BufferItem item2;
                        item2.m_position.m_segment = segmentID;
                        item2.m_position.m_lane = (byte)num12;
                        item2.m_position.m_offset = (byte)(((direction & NetInfo.Direction.Forward) == 0) ? 0 : 255);
                        if ((byte)(lane2.m_laneType & laneType) == 0)
                        {
                            item2.m_methodDistance = 0f;
                        }
                        else
                        {
                            item2.m_methodDistance = num9 + num13;
                        }
                        if (lane2.m_laneType != NetInfo.LaneType.Pedestrian || item2.m_methodDistance < 1000f)
                        {
                            item2.m_comparisonValue = num10 + num14;
                            item2.m_direction = direction;
                            if (num2 == this.m_startLaneA)
                            {
                                if (((byte)(item2.m_direction & NetInfo.Direction.Forward) == 0 || item2.m_position.m_offset < this.m_startOffsetA) && ((byte)(item2.m_direction & NetInfo.Direction.Backward) == 0 || item2.m_position.m_offset > this.m_startOffsetA))
                                {
                                    num2 = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_nextLane;
                                    num12++;
                                    continue;
                                }
                                float num15 = this.CalculateLaneSpeed(this.m_startOffsetA, item2.m_position.m_offset, ref segment, lane2, num2);
                                float num16 = (float)Mathf.Abs((int)(item2.m_position.m_offset - this.m_startOffsetA)) * 0.003921569f;
                                item2.m_comparisonValue += num16 * segment.m_averageLength / (num15 * this.m_maxLength);
                            }
                            if (num2 == this.m_startLaneB)
                            {
                                if (((byte)(item2.m_direction & NetInfo.Direction.Forward) == 0 || item2.m_position.m_offset < this.m_startOffsetB) && ((byte)(item2.m_direction & NetInfo.Direction.Backward) == 0 || item2.m_position.m_offset > this.m_startOffsetB))
                                {
                                    num2 = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_nextLane;
                                    num12++;
                                    continue;
                                }
                                float num17 = this.CalculateLaneSpeed(this.m_startOffsetB, item2.m_position.m_offset, ref segment, lane2, num2);
                                float num18 = (float)Mathf.Abs((int)(item2.m_position.m_offset - this.m_startOffsetB)) * 0.003921569f;
                                item2.m_comparisonValue += num18 * segment.m_averageLength / (num17 * this.m_maxLength);
                            }
                            if (!this.m_ignoreBlocked && (segment.m_flags & NetSegment.Flags.Blocked) != NetSegment.Flags.None && (byte)(lane2.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0)
                            {
                                item2.m_comparisonValue += 0.1f;
                                result = true;
                            }
                            item2.m_lanesUsed = (item.m_lanesUsed | lane2.m_laneType);
                            item2.m_laneID = num2;
                            if ((byte)(lane2.m_laneType & laneType) != 0 && (lane2.m_vehicleType & this.m_vehicleTypes) != VehicleInfo.VehicleType.None)
                            {
                                int firstTarget = (int)instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_firstTarget;
                                int lastTarget = (int)instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_lastTarget;
                                if (currentTargetIndex < firstTarget || currentTargetIndex >= lastTarget)
                                {
                                    item2.m_comparisonValue += Mathf.Max(1f, num13 * 3f - 3f) / ((num5 + LaneManager.GetLaneSpeed(num2, lane2)) * 0.5f * this.m_maxLength);
                                }
                                if (!this.m_transportVehicle && lane2.m_laneType == NetInfo.LaneType.TransportVehicle)
                                {
                                    item2.m_comparisonValue += 20f / ((num5 + lane2.m_speedLimit) * 0.5f * this.m_maxLength);
                                }
                            }
                            this.AddBufferItem(item2, item.m_position);
                        }
                    }
                    num2 = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_nextLane;
                    num12++;
                    continue;
                }
                if ((byte)(lane2.m_laneType & laneType) != 0 && (lane2.m_vehicleType & vehicleType) != VehicleInfo.VehicleType.None)
                {
                    num11++;
                }
                num2 = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_nextLane;
                num12++;
            }
            currentTargetIndex = num11;
            return result;
        }

        private void ProcessItem(BufferItem item, ushort targetNode, ushort segmentID, ref NetSegment segment, byte connectOffset, int laneIndex, uint lane)
        {
            if ((segment.m_flags & (NetSegment.Flags.PathFailed | NetSegment.Flags.Flooded)) != NetSegment.Flags.None)
            {
                return;
            }
            NetManager instance = Singleton<NetManager>.instance;
            NetInfo info = segment.Info;
            NetInfo info2 = instance.m_segments.m_buffer[(int)item.m_position.m_segment].Info;
            int num = info.m_lanes.Length;
            float num2;
            byte offset;
            if (segmentID == item.m_position.m_segment)
            {
                Vector3 b = instance.m_lanes.m_buffer[(int)((UIntPtr)item.m_laneID)].CalculatePosition((float)connectOffset * 0.003921569f);
                Vector3 a = instance.m_lanes.m_buffer[(int)((UIntPtr)lane)].CalculatePosition((float)connectOffset * 0.003921569f);
                num2 = Vector3.Distance(a, b);
                offset = connectOffset;
            }
            else
            {
                NetInfo.Direction direction = (targetNode != segment.m_startNode) ? NetInfo.Direction.Forward : NetInfo.Direction.Backward;
                Vector3 b2 = instance.m_lanes.m_buffer[(int)((UIntPtr)item.m_laneID)].CalculatePosition((float)connectOffset * 0.003921569f);
                Vector3 a2;
                if ((byte)(direction & NetInfo.Direction.Forward) != 0)
                {
                    a2 = instance.m_lanes.m_buffer[(int)((UIntPtr)lane)].m_bezier.d;
                }
                else
                {
                    a2 = instance.m_lanes.m_buffer[(int)((UIntPtr)lane)].m_bezier.a;
                }
                num2 = Vector3.Distance(a2, b2);
                offset = (byte)(((direction & NetInfo.Direction.Forward) == 0) ? 0 : 255);
            }
            float num3 = 1f;
            float num4 = 1f;
            NetInfo.LaneType laneType = NetInfo.LaneType.None;
            if ((int)item.m_position.m_lane < info2.m_lanes.Length)
            {
                uint l = instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_lanes;
                for (int n = 0; l != 0 && n < (int)item.m_position.m_lane; ++n)
                    l = instance.m_lanes.m_buffer[l].m_nextLane;

                NetInfo.Lane lane2 = info2.m_lanes[(int)item.m_position.m_lane];
                num3 = LaneManager.GetLaneSpeed(l, lane2);
                laneType = lane2.m_laneType;
                if ((byte)(laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0)
                {
                    laneType = (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle);
                }
                num4 = this.CalculateLaneSpeed(connectOffset, item.m_position.m_offset, ref instance.m_segments.m_buffer[(int)item.m_position.m_segment], lane2, l);
            }
            float averageLength = instance.m_segments.m_buffer[(int)item.m_position.m_segment].m_averageLength;
            float num5 = (float)Mathf.Abs((int)(connectOffset - item.m_position.m_offset)) * 0.003921569f * averageLength;
            float num6 = item.m_methodDistance + num5;
            float num7 = item.m_comparisonValue + num5 / (num4 * this.m_maxLength);
            if (laneIndex < num)
            {
                NetInfo.Lane lane3 = info.m_lanes[laneIndex].ShallowClone();
                BufferItem item2;
                item2.m_position.m_segment = segmentID;
                item2.m_position.m_lane = (byte)laneIndex;
                item2.m_position.m_offset = offset;
                if ((byte)(lane3.m_laneType & laneType) == 0)
                {
                    item2.m_methodDistance = 0f;
                }
                else
                {
                    if (item.m_methodDistance == 0f)
                    {
                        num7 += 100f / (0.25f * this.m_maxLength);
                    }
                    item2.m_methodDistance = num6 + num2;
                }
                if (lane3.m_laneType != NetInfo.LaneType.Pedestrian || item2.m_methodDistance < 1000f)
                {
                    item2.m_comparisonValue = num7 + num2 / ((num3 + LaneManager.GetLaneSpeed(lane, lane3)) * 0.25f * this.m_maxLength);
                    if ((segment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None)
                    {
                        item2.m_direction = NetInfo.InvertDirection(lane3.m_finalDirection);
                    }
                    else
                    {
                        item2.m_direction = lane3.m_finalDirection;
                    }
                    if (lane == this.m_startLaneA)
                    {
                        if (((byte)(item2.m_direction & NetInfo.Direction.Forward) == 0 || item2.m_position.m_offset < this.m_startOffsetA) && ((byte)(item2.m_direction & NetInfo.Direction.Backward) == 0 || item2.m_position.m_offset > this.m_startOffsetA))
                        {
                            return;
                        }
                        float num8 = this.CalculateLaneSpeed(this.m_startOffsetA, item2.m_position.m_offset, ref segment, lane3, lane);
                        float num9 = (float)Mathf.Abs((int)(item2.m_position.m_offset - this.m_startOffsetA)) * 0.003921569f;
                        item2.m_comparisonValue += num9 * segment.m_averageLength / (num8 * this.m_maxLength);
                    }
                    if (lane == this.m_startLaneB)
                    {
                        if (((byte)(item2.m_direction & NetInfo.Direction.Forward) == 0 || item2.m_position.m_offset < this.m_startOffsetB) && ((byte)(item2.m_direction & NetInfo.Direction.Backward) == 0 || item2.m_position.m_offset > this.m_startOffsetB))
                        {
                            return;
                        }
                        float num10 = this.CalculateLaneSpeed(this.m_startOffsetB, item2.m_position.m_offset, ref segment, lane3, lane);
                        float num11 = (float)Mathf.Abs((int)(item2.m_position.m_offset - this.m_startOffsetB)) * 0.003921569f;
                        item2.m_comparisonValue += num11 * segment.m_averageLength / (num10 * this.m_maxLength);
                    }
                    item2.m_laneID = lane;
                    item2.m_lanesUsed = (item.m_lanesUsed | lane3.m_laneType);
                    this.AddBufferItem(item2, item.m_position);
                }
            }
        }

        private void AddBufferItem(BufferItem item, PathUnit.Position target)
        {
            uint num = this.m_laneLocation[(int)((UIntPtr)item.m_laneID)];
            uint num2 = num >> 16;
            int num3 = (int)(num & 65535u);
            int num6;
            if (num2 == this.m_pathFindIndex)
            {
                if (item.m_comparisonValue >= this.m_buffer[num3].m_comparisonValue)
                {
                    return;
                }
                int num4 = num3 >> 6;
                int num5 = num3 & -64;
                if (num4 < this.m_bufferMinPos || (num4 == this.m_bufferMinPos && num5 < this.m_bufferMin[num4]))
                {
                    return;
                }
                num6 = Mathf.Max(Mathf.RoundToInt(item.m_comparisonValue * 1024f), this.m_bufferMinPos);
                if (num6 == num4)
                {
                    this.m_buffer[num3] = item;
                    this.m_laneTarget[(int)((UIntPtr)item.m_laneID)] = target;
                    return;
                }
                int num7 = num4 << 6 | this.m_bufferMax[num4]--;
                BufferItem bufferItem = this.m_buffer[num7];
                this.m_laneLocation[(int)((UIntPtr)bufferItem.m_laneID)] = num;
                this.m_buffer[num3] = bufferItem;
            }
            else
            {
                num6 = Mathf.Max(Mathf.RoundToInt(item.m_comparisonValue * 1024f), this.m_bufferMinPos);
            }
            if (num6 >= 1024)
            {
                return;
            }
            while (this.m_bufferMax[num6] == 63)
            {
                num6++;
                if (num6 == 1024)
                {
                    return;
                }
            }
            if (num6 > this.m_bufferMaxPos)
            {
                this.m_bufferMaxPos = num6;
            }
            num3 = (num6 << 6 | ++this.m_bufferMax[num6]);
            this.m_buffer[num3] = item;
            this.m_laneLocation[(int)((UIntPtr)item.m_laneID)] = (this.m_pathFindIndex << 16 | (uint)num3);
            this.m_laneTarget[(int)((UIntPtr)item.m_laneID)] = target;
        }

		private void GetLaneDirection(PathUnit.Position pathPos, out NetInfo.Direction direction, out NetInfo.LaneType type)
		{
		    NetManager instance = Singleton<NetManager>.instance;
		    NetInfo info = instance.m_segments.m_buffer[(int)pathPos.m_segment].Info;
		    if (info.m_lanes.Length > (int)pathPos.m_lane)
		    {
			    direction = info.m_lanes[(int)pathPos.m_lane].m_finalDirection;
			    type = info.m_lanes[(int)pathPos.m_lane].m_laneType;
			    if ((instance.m_segments.m_buffer[(int)pathPos.m_segment].m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None)
			    {
				    direction = NetInfo.InvertDirection(direction);
			    }
		    }
		    else
		    {
			    direction = NetInfo.Direction.None;
			    type = NetInfo.LaneType.None;
		    }
		}

		private void PathFindThread()
	    {
		    while (true)
		    {
			    while (!Monitor.TryEnter(this.m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
			    {
			    }
			    try
			    {
				    while (this.m_queueFirst == 0u && !this.m_terminated)
				    {
					    Monitor.Wait(this.m_queueLock);
				    }
				    if (this.m_terminated)
				    {
					    break;
				    }
				    this.m_calculating = this.m_queueFirst;
				    this.m_queueFirst = this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_nextPathUnit;
				    if (this.m_queueFirst == 0u)
				    {
					    this.m_queueLast = 0u;
					    Facade.m_queuedPathFindCount = 0;
				    }
				    else
				    {
                        Facade.m_queuedPathFindCount--;
				    }
				    this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_nextPathUnit = 0u;
				    this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_pathFindFlags = (byte)(((int)this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_pathFindFlags & -2) | 2);
			    }
			    finally
			    {
				    Monitor.Exit(this.m_queueLock);
			    }
			    try
			    {
                    Facade.m_pathfindProfiler.BeginStep();
				    try
				    {
					    this.PathFindImplementation(this.m_calculating, ref this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)]);
				    }
				    finally
				    {
                        Facade.m_pathfindProfiler.EndStep();
				    }
			    }
			    catch (Exception ex)
			    {
				    UIView.ForwardException(ex);
				    CODebugBase<LogChannel>.Error(LogChannel.Core, "Path find error: " + ex.Message + "\n" + ex.StackTrace);
				    PathUnit[] expr_1A0_cp_0 = this.m_pathUnits.m_buffer;
				    UIntPtr expr_1A0_cp_1 = (UIntPtr)this.m_calculating;
				    expr_1A0_cp_0[(int)expr_1A0_cp_1].m_pathFindFlags = (byte)(expr_1A0_cp_0[(int)expr_1A0_cp_1].m_pathFindFlags | 8);
			    }
			    while (!Monitor.TryEnter(this.m_queueLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
			    {
			    }
			    try
			    {
				    this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_pathFindFlags = (byte)((int)this.m_pathUnits.m_buffer[(int)((UIntPtr)this.m_calculating)].m_pathFindFlags & -3);
				    Singleton<PathManager>.instance.ReleasePath(this.m_calculating);
				    this.m_calculating = 0u;
				    Monitor.Pulse(this.m_queueLock);
			    }
			    finally
			    {
				    Monitor.Exit(this.m_queueLock);
			    }
		    }
	    }
    }   
}