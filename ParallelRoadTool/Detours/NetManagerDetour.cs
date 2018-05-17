﻿using ColossalFramework;
using ColossalFramework.Math;
using ParallelRoadTool.Redirection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ParallelRoadTool.Detours
{
    unsafe public struct NetManagerDetour
    {

        private static MethodInfo from = typeof(NetManager).GetMethod("CreateSegment", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static MethodInfo to = typeof(NetManagerDetour).GetMethod("CreateSegment", BindingFlags.NonPublic | BindingFlags.Instance);

        private static RedirectCallsState m_state;
        private static bool m_deployed = false;

        private static NetManager _netManager = Singleton<NetManager>.instance;
        private static Randomizer _randomizer = Singleton<SimulationManager>.instance.m_randomizer;

        // We store nodes from previous iteration so that we know which node to connect to
        private static ushort? _endNodeId, _clonedEndNodeId;

        public static bool IsDeployed() => m_deployed;

        public static void Deploy()
        {
            if (!m_deployed)
            {
                m_state = RedirectionHelper.RedirectCalls(from, to);
                m_deployed = true;
            }
        }

        public static void Revert()
        {
            if (m_deployed)
            {
                RedirectionHelper.RevertRedirect(from, m_state);
                m_deployed = false;
            }
        }

        #region Utility

        /// <summary>
        /// Given a point, a direction and a distance, we can get the coordinates for a point which is parallel to the given one for the given direction. 
        /// </summary>
        /// <param name="point"></param>
        /// <param name="direction"></param>
        /// <param name="distance"></param>
        /// <param name="isClockwise"></param>
        /// <returns>A <see cref="Vector3"/> with the coordinates generated by offsetting the given point.</returns>
        private Vector3 Offset(Vector3 point, Vector3 direction, float distance, bool isClockwise = true)
        {
            var offsetPoint = point + distance * new Vector3((isClockwise ? 1 : -1) * direction.z, direction.y, (isClockwise ? -1 : 1) * direction.x);
            offsetPoint.y = point.y;

            return offsetPoint;
        }

        /// <summary>
        /// This methods skips our detour by calling the original method from the game, allowing the creation of the needed segment.
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="randomizer"></param>
        /// <param name="info"></param>
        /// <param name="startNode"></param>
        /// <param name="endNode"></param>
        /// <param name="startDirection"></param>
        /// <param name="endDirection"></param>
        /// <param name="buildIndex"></param>
        /// <param name="modifiedIndex"></param>
        /// <param name="invert"></param>
        /// <returns></returns>
        private bool CreateSegmentOriginal(out ushort segment, ref Randomizer randomizer, NetInfo info, ushort startNode, ushort endNode, Vector3 startDirection, Vector3 endDirection, uint buildIndex, uint modifiedIndex, bool invert)
        {
            Revert();

            var result = NetManager.instance.CreateSegment(out segment, ref randomizer, info, startNode, endNode, startDirection, endDirection, buildIndex, modifiedIndex, invert);

            Deploy();

            return result;
        }

        #endregion

        /// <summary>
        /// Mod's core.
        /// First, we create the segment using game's original code.
        /// Then we offset the 2 nodes of the segment, based on both direction and curve, so that we can finally create a segment between the 2 offset nodes.
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="randomizer"></param>
        /// <param name="info"></param>
        /// <param name="startNode"></param>
        /// <param name="endNode"></param>
        /// <param name="startDirection"></param>
        /// <param name="endDirection"></param>
        /// <param name="buildIndex"></param>
        /// <param name="modifiedIndex"></param>
        /// <param name="invert"></param>
        /// <returns></returns>
        private bool CreateSegment(out ushort segment, ref Randomizer randomizer, NetInfo info, ushort startNode, ushort endNode, Vector3 startDirection, Vector3 endDirection, uint buildIndex, uint modifiedIndex, bool invert)
        {

            // Let's create the segment that the user requested
            CreateSegmentOriginal(out segment, ref randomizer, info, startNode, endNode, startDirection, endDirection, buildIndex, modifiedIndex, invert);            

            // TODO: support for multiple networks, we need to loop from there to the end
            // If the user didn't select a NetInfo we'll use the one he's using for the main road
            var selectedNetInfo = ParallelRoadTool.SelectedRoadTypes[0] ?? info;
            DebugUtils.Log($"Using netInfo {selectedNetInfo.name}");

            // TODO: we have to get this from the UI but the UI is not showing the textinput right now. Also, we need to input that in meters and convert it into units.
            var offset = 15f;
            DebugUtils.Log($"Using offset {offset}");

            // Get original nodes to clone them
            var startNetNode = NetManager.instance.m_nodes.m_buffer[startNode];
            var endNetNode = NetManager.instance.m_nodes.m_buffer[endNode];

            // Create two clone nodes by offsetting the original ones.
            // If we're not in "invert" mode (aka final part of a curve) and we already have an ending node with the same id of our starting node, we need to use that so that the segments can be connected
            // If we don't have any previous node matching our starting one, we need to clone startNode as this may be a new segment
            ushort newStartNodeId;
            if (!invert && _endNodeId.HasValue && _endNodeId.Value == startNode)
            {
                DebugUtils.Log($"Using old node from previous iteration {_clonedEndNodeId.Value} instead of the given one {startNode}");
                newStartNodeId = _clonedEndNodeId.Value;
                DebugUtils.Log($"Start node{startNetNode.m_position} becomes {NetManager.instance.m_nodes.m_buffer[newStartNodeId].m_position}");
            }
            else
            {
                var newStartPosition = Offset(startNetNode.m_position, startDirection, offset, invert);
                DebugUtils.Log($"{startNetNode.m_position} --> {newStartPosition}");
                NetManager.instance.CreateNode(out newStartNodeId, ref randomizer, info, newStartPosition, Singleton<SimulationManager>.instance.m_currentBuildIndex + 1);
            }

            // Same thing as startNode, but this time we don't clone if we're in "invert" mode as we may need to connect this ending node with the previous ending one.
            ushort newEndNodeId;
            if (invert && _endNodeId.HasValue && _endNodeId.Value == endNode)
            {
                DebugUtils.Log($"Using old node from previous iteration {_clonedEndNodeId.Value} instead of the given one {endNode}");
                newEndNodeId = _clonedEndNodeId.Value;
                DebugUtils.Log($"Start node{endNetNode.m_position} becomes {NetManager.instance.m_nodes.m_buffer[newEndNodeId].m_position}");
            }
            else
            {
                var newEndPosition = Offset(endNetNode.m_position, endDirection, offset);
                DebugUtils.Log($"{endNetNode.m_position} --> {newEndPosition}");
                NetManager.instance.CreateNode(out newEndNodeId, ref randomizer, info, newEndPosition, Singleton<SimulationManager>.instance.m_currentBuildIndex + 1);
            }

            // TODO: if curve has more than two segments they won't be connected

            // Store current end nodes in case we may need to connect the following segment to them
            _endNodeId = endNode;
            _clonedEndNodeId = newEndNodeId;

            // Create the segment between the two cloned nodes
            return CreateSegmentOriginal(out segment, ref randomizer, selectedNetInfo, newStartNodeId, newEndNodeId, startDirection, endDirection, Singleton<SimulationManager>.instance.m_currentBuildIndex + 1, Singleton<SimulationManager>.instance.m_currentBuildIndex, invert);
        }
    }
}
