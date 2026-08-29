using System;
using System.Collections.Generic;
using Mapbox.BaseModule.Data;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Utilities;
using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.MeshModifiers
{
    /// <summary>
    /// Per building, writes two pieces of per-building data into UV channels so the BuildingShrink
    /// shader can (a) shrink the whole building uniformly and (b) tint it with a stable random
    /// color offset — both SURVIVING "Merge Objects" because UV channels are concatenated per
    /// vertex in CombineMeshData.
    ///
    /// Channels written (every vertex of a building carries the SAME values):
    ///   UvChannel      (default 1 / TEXCOORD1): ANCHOR = object-space centroid XZ. Used for the
    ///                  cone/shrink test.
    ///   UvChannel + 1  (default 2 / TEXCOORD2): TINT SEED = two stable random values in [-1, 1],
    ///                  derived from the feature id so a building keeps the same tint across
    ///                  reloads. The shader expands these into an RGB offset around the base color.
    ///
    /// ORDER MATTERS: place this AFTER the Polygon Mesh Modifier and Height Modifier in the stack,
    /// so md.Vertices is the finished building geometry when this runs. UV0 is never touched.
    /// </summary>
    [Serializable]
    public class BuildingAnchorMeshModifier : MeshModifier
    {
        private readonly int _anchorChannel;
        private readonly int _tintChannel;

        public BuildingAnchorMeshModifier(int uvChannel)
        {
            _anchorChannel = Mathf.Max(1, uvChannel); // never overwrite UV0
            _tintChannel = _anchorChannel + 1;
        }

        public override void Run(VectorFeatureUnity feature, MeshData md, IMapInformation mapInfo)
        {
            if (md == null || md.Vertices == null) return;
            int vCount = md.Vertices.Count;
            if (vCount == 0) return;

            // --- Anchor: object-space centroid XZ ---
            double sx = 0.0, sz = 0.0;
            for (int i = 0; i < vCount; i++)
            {
                Vector3 v = md.Vertices[i];
                sx += v.x;
                sz += v.z;
            }
            Vector2 anchor = new Vector2((float)(sx / vCount), (float)(sz / vCount));

            // --- Tint seed: two stable random values in [-1, 1], deterministic per building ---
            uint seed = GetFeatureSeed(feature);
            Vector2 tint = new Vector2(Rand11(ref seed), Rand11(ref seed));

            WriteChannel(md, _anchorChannel, anchor, vCount);
            WriteChannel(md, _tintChannel, tint, vCount);
        }

        // Writes `value` into UV channel `channelIndex` for every one of the building's vertices,
        // sizing the channel to exactly vCount so it stays aligned through CombineMeshData.
        private static void WriteChannel(MeshData md, int channelIndex, Vector2 value, int vCount)
        {
            while (md.UV.Count <= channelIndex)
                md.UV.Add(new List<Vector2>());

            List<Vector2> channel = md.UV[channelIndex];

            if (channel.Count < vCount)
            {
                for (int i = channel.Count; i < vCount; i++)
                    channel.Add(value);
            }
            else if (channel.Count > vCount)
            {
                channel.RemoveRange(vCount, channel.Count - vCount);
            }

            for (int i = 0; i < vCount; i++)
                channel[i] = value;
        }

        // Stable seed per building from the feature id (falls back to hashing the first point).
        private static uint GetFeatureSeed(VectorFeatureUnity feature)
        {
            ulong id = 0;
            if (feature != null && feature.Data != null)
                id = feature.Data.Id;

            if (id == 0 && feature != null && feature.Points != null && feature.Points.Count > 0
                && feature.Points[0].Count > 0)
            {
                Vector3 p = feature.Points[0][0];
                id = (ulong)(Mathf.RoundToInt(p.x * 100000f))
                   ^ ((ulong)(Mathf.RoundToInt(p.z * 100000f)) << 21);
            }

            uint s = (uint)(id ^ (id >> 32));
            if (s == 0) s = 0x9E3779B9u;
            return s;
        }

        // xorshift32 -> float in [-1, 1].
        private static float Rand11(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state / (float)uint.MaxValue) * 2f - 1f;
        }
    }
}