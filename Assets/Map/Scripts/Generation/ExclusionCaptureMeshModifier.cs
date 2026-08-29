using System;
using System.Collections.Generic;
using Mapbox.BaseModule.Data;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Utilities;
using Mapbox.VectorModule.MeshGeneration.GameObjectModifiers; // ExclusionRegistry + ExclusionGeometryType
using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.MeshModifiers
{
    [Serializable]
    public class ExclusionCaptureMeshModifier : MeshModifier
    {
        private readonly ExclusionGeometryType _type;
        private readonly float _simplifyEpsilon;
        private readonly int _minTileZoom;
        private readonly int _maxTileZoom;
        private readonly bool _includeTunnels;
        private readonly bool _includeFerryRoutes;
        private readonly bool _includeUndergroundBuildings;

        public ExclusionCaptureMeshModifier(ExclusionGeometryType type, float simplifyEpsilon = 0f,
            int minTileZoom = 0, int maxTileZoom = 22,
            bool includeTunnels = false, bool includeFerryRoutes = false,
            bool includeUndergroundBuildings = false)
        {
            _type = type;
            _simplifyEpsilon = simplifyEpsilon;
            _minTileZoom = minTileZoom;
            _maxTileZoom = maxTileZoom;
            _includeTunnels = includeTunnels;
            _includeFerryRoutes = includeFerryRoutes;
            _includeUndergroundBuildings = includeUndergroundBuildings;
        }

        public override void Run(VectorFeatureUnity feature, MeshData md, IMapInformation mapInfo)
        {
            if (feature == null) return;

            int tileZoom = feature.TileId.Z;
            if (tileZoom < _minTileZoom || tileZoom > _maxTileZoom)
                return;

            // INVISIBLE-FEATURE GUARD. The vector layers contain features that are real data
            // but render nothing on a typical map: road tunnels (structure=tunnel), ferry
            // routes (class=ferry, lines drawn across open water), and underground structures
            // in the building layer (underground=true — metro stations, basements, underpasses).
            // Capturing those produces exclusion zones with nothing visible in them: bare
            // stripes and blobs where scatter refuses to spawn and nobody can see why. Skip
            // them unless explicitly included.
            if (_type == ExclusionGeometryType.Line)
            {
                if (!_includeTunnels && PropertyIs(feature, "structure", "tunnel")) return;
                if (!_includeFerryRoutes && PropertyIs(feature, "class", "ferry")) return;
            }
            else
            {
                if (!_includeUndergroundBuildings && PropertyIsTrue(feature, "underground")) return;
            }

            if (_type == ExclusionGeometryType.Polygon)
            {
                // Read the triangulated mesh produced by the Polygon Mesh Modifier that runs
                // BEFORE this one in the same stack. The SDK's triangulator has already
                // resolved multiple polygons and holes; we copy vertices and flatten the
                // (possibly multi-submesh) triangle lists. Coordinates are normalized tile
                // space, matching the scatter samplers.
                if (md == null || md.Vertices == null || md.Vertices.Count < 3 ||
                    md.Triangles == null || md.Triangles.Count == 0)
                    return;

                var verts = new List<Vector3>(md.Vertices); // copy: md is reused downstream
                int triTotal = 0;
                foreach (var sub in md.Triangles)
                    if (sub != null) triTotal += sub.Count;

                var tris = new List<int>(triTotal);
                foreach (var sub in md.Triangles)
                    if (sub != null) tris.AddRange(sub);

                if (tris.Count >= 3)
                    ExclusionRegistry.AddMesh(feature.TileId, verts, tris);
            }
            else // Line — roads have no fill mesh; store raw polylines for distance testing.
            {
                if (feature.Points == null || feature.Points.Count == 0) return;

                var lines = new List<List<Vector3>>(feature.Points.Count);
                foreach (var line in feature.Points)
                {
                    if (line == null || line.Count < 2) continue;
                    var simplified = _simplifyEpsilon > 0f
                        ? SimplifyPolyline(line, _simplifyEpsilon)
                        : new List<Vector3>(line);
                    if (simplified.Count >= 2)
                        lines.Add(simplified);
                }
                if (lines.Count > 0)
                    ExclusionRegistry.AddLines(feature.TileId, lines);
            }
        }

        // Property values arrive as strings or native types depending on the decode path, so
        // compare via ToString, case-insensitively.
        private static bool PropertyIs(VectorFeatureUnity feature, string key, string value)
        {
            if (feature.Properties == null) return false;
            return feature.Properties.TryGetValue(key, out var v) && v != null &&
                   string.Equals(v.ToString(), value, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PropertyIsTrue(VectorFeatureUnity feature, string key)
        {
            if (feature.Properties == null) return false;
            if (!feature.Properties.TryGetValue(key, out var v) || v == null) return false;
            if (v is bool b) return b;
            return string.Equals(v.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        // Douglas–Peucker line simplification on the x/z plane, iterative to avoid stack
        // overflow on very long road polylines.
        private static List<Vector3> SimplifyPolyline(List<Vector3> pts, float epsilon)
        {
            int n = pts.Count;
            if (n < 3) return new List<Vector3>(pts);

            float epsSq = epsilon * epsilon;
            var keep = new bool[n];
            keep[0] = true;
            keep[n - 1] = true;

            var stack = new Stack<(int start, int end)>();
            stack.Push((0, n - 1));

            while (stack.Count > 0)
            {
                var (start, end) = stack.Pop();
                if (end <= start + 1) continue;

                float ax = pts[start].x, az = pts[start].z;
                float bx = pts[end].x, bz = pts[end].z;
                float maxDistSq = -1f;
                int maxIdx = -1;
                for (int i = start + 1; i < end; i++)
                {
                    float d = PointSegDistSq(pts[i].x, pts[i].z, ax, az, bx, bz);
                    if (d > maxDistSq) { maxDistSq = d; maxIdx = i; }
                }

                if (maxDistSq > epsSq && maxIdx > 0)
                {
                    keep[maxIdx] = true;
                    stack.Push((start, maxIdx));
                    stack.Push((maxIdx, end));
                }
            }

            var result = new List<Vector3>(n);
            for (int i = 0; i < n; i++)
                if (keep[i]) result.Add(pts[i]);
            return result;
        }

        private static float PointSegDistSq(float px, float pz, float ax, float az, float bx, float bz)
        {
            float dx = bx - ax, dz = bz - az;
            float lenSq = dx * dx + dz * dz;
            float t = lenSq > 0f ? ((px - ax) * dx + (pz - az) * dz) / lenSq : 0f;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float cx = ax + t * dx, cz = az + t * dz;
            float ex = px - cx, ez = pz - cz;
            return ex * ex + ez * ez;
        }
    }
}