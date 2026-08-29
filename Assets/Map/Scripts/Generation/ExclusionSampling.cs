using System.Collections.Generic;
using Mapbox.BaseModule.Data.Tiles;
using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.GameObjectModifiers
{
    /// <summary>
    /// The geometry and selection maths shared by every consumer of ExclusionRegistry —
    /// currently DeferredSpawner (park scatter) and FlowerFieldManager (flower field).
    ///
    /// These tests used to live privately in each consumer. Keeping one copy matters more
    /// than it looks: the two implementations had already drifted (strict vs non-strict
    /// distance comparisons, one missing the zero-buffer early-out), and any future fix to
    /// the road-buffer or margin maths needs to land in both places or the two scatterers
    /// quietly disagree about where the ground is.
    ///
    /// EVERYTHING HERE WORKS IN NORMALIZED TILE SPACE: x in [0,1], z in [0,-1], one tile
    /// spanning ~1 unit. MarginSq and RoadBufferSq are in those units, not metres.
    /// </summary>
    public static class ExclusionSampling
    {
        /// <summary>
        /// A caller-private view of one tile's exclusion geometry.
        ///
        /// WHY THIS EXISTS: capture runs on background threads, so iterating the registry's
        /// LIVE lists races with AddMesh/AddLines. That race is easy to overlook because it
        /// looks safe — all mesh modifiers for a tile finish before any GameObject modifier
        /// for that tile runs. But both consumers hold their exclusion data across FRAMES
        /// (the deferred spawner by design, the flower field because it re-reads whenever the
        /// player moves), and a tile that unloads and reloads will append into the same
        /// TileExclusions object while an old task is still walking it.
        ///
        /// The ExclusionMesh / ExclusionLine objects are immutable once constructed, so
        /// copying the outer list is enough — no deep copy, no per-point cost.
        /// </summary>
        public class Snapshot
        {
            public readonly List<ExclusionRegistry.ExclusionMesh> Meshes = new();
            public readonly List<ExclusionRegistry.ExclusionLine> Lines = new();

            public bool IsEmpty => Meshes.Count == 0 && Lines.Count == 0;

            public void Clear()
            {
                Meshes.Clear();
                Lines.Clear();
            }
        }

        /// <summary>
        /// Fills a reusable snapshot buffer. For callers that consume the data immediately
        /// and can share one buffer across calls (the flower field, one cell at a time).
        /// Returns false — and leaves the snapshot empty — if the tile has no entry.
        /// </summary>
        public static bool TryTake(CanonicalTileId tileId, Snapshot into)
        {
            return ExclusionRegistry.TryGetSnapshot(tileId, into.Meshes, into.Lines, out _);
        }

        /// <summary>As above, plus the registry version the data was taken at.</summary>
        public static bool TryTake(CanonicalTileId tileId, Snapshot into, out int version)
        {
            return ExclusionRegistry.TryGetSnapshot(tileId, into.Meshes, into.Lines, out version);
        }

        /// <summary>
        /// Allocating variant, for callers that must keep the snapshot alive across frames
        /// (the deferred scatter task). Returns null if the tile has no entry, which callers
        /// use to distinguish "nothing to avoid" from "avoid these things".
        /// </summary>
        public static Snapshot Take(CanonicalTileId tileId)
        {
            var s = new Snapshot();
            return ExclusionRegistry.TryGetSnapshot(tileId, s.Meshes, s.Lines) ? s : null;
        }

        /// <summary>
        /// True if the point falls inside any captured polygon (water, buildings), within
        /// `marginSq` of a polygon edge, or within `roadBufferSq` of a captured road
        /// centreline. AABB early-out per mesh/line first — that is what keeps road-dense
        /// tiles affordable, since most points never touch most geometry.
        /// </summary>
        public static bool IsExcluded(Snapshot snapshot, float px, float pz,
                                      float marginSq, float roadBufferSq)
        {
            if (snapshot == null) return false;

            float margin = marginSq > 0f ? Mathf.Sqrt(marginSq) : 0f;
            var meshes = snapshot.Meshes;
            for (int i = 0; i < meshes.Count; i++)
            {
                var m = meshes[i];
                if (px < m.MinX - margin || px > m.MaxX + margin ||
                    pz < m.MinZ - margin || pz > m.MaxZ + margin)
                    continue;

                var verts = m.Verts;
                var tris = m.Tris;
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    var a = verts[tris[t]];
                    var b = verts[tris[t + 1]];
                    var c = verts[tris[t + 2]];

                    if (PointInTriangle(px, pz, a, b, c))
                        return true;

                    // Dilate the polygon by testing distance to each edge, so objects don't
                    // spawn hard against a shoreline or wall.
                    if (marginSq > 0f)
                    {
                        if (PointSegmentDistanceSq(px, pz, a.x, a.z, b.x, b.z) <= marginSq) return true;
                        if (PointSegmentDistanceSq(px, pz, b.x, b.z, c.x, c.z) <= marginSq) return true;
                        if (PointSegmentDistanceSq(px, pz, c.x, c.z, a.x, a.z) <= marginSq) return true;
                    }
                }
            }

            if (roadBufferSq > 0f)
            {
                float buf = Mathf.Sqrt(roadBufferSq);
                var lines = snapshot.Lines;
                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    if (px < line.MinX - buf || px > line.MaxX + buf ||
                        pz < line.MinZ - buf || pz > line.MaxZ + buf)
                        continue;

                    var pts = line.Points;
                    for (int j = 0; j < pts.Count - 1; j++)
                        if (PointSegmentDistanceSq(px, pz, pts[j].x, pts[j].z, pts[j + 1].x, pts[j + 1].z) <= roadBufferSq)
                            return true;
                }
            }

            return false;
        }

        public static bool PointInTriangle(float px, float pz, Vector3 a, Vector3 b, Vector3 c)
        {
            float d1 = Sign(px, pz, a.x, a.z, b.x, b.z);
            float d2 = Sign(px, pz, b.x, b.z, c.x, c.z);
            float d3 = Sign(px, pz, c.x, c.z, a.x, a.z);
            bool hasNeg = (d1 < 0f) || (d2 < 0f) || (d3 < 0f);
            bool hasPos = (d1 > 0f) || (d2 > 0f) || (d3 > 0f);
            // Winding-agnostic: inside means all three cross products share a sign. The SDK's
            // triangulator output isn't guaranteed to be consistently wound.
            return !(hasNeg && hasPos);
        }

        private static float Sign(float px, float pz, float ax, float az, float bx, float bz)
        {
            return (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
        }

        public static float PointSegmentDistanceSq(float px, float pz,
                                                   float ax, float az, float bx, float bz)
        {
            float dx = bx - ax, dz = bz - az;
            float lenSq = dx * dx + dz * dz;
            float t = lenSq > 0f ? ((px - ax) * dx + (pz - az) * dz) / lenSq : 0f;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float cx = ax + t * dx, cz = az + t * dz;
            float ex = px - cx, ez = pz - cz;
            return ex * ex + ez * ez;
        }

        // ---- weighted selection ----

        /// <summary>
        /// Builds a cumulative weight table parallel to the item array. Returns null when
        /// weights aren't configured, or when they're all zero — both meaning "pick
        /// uniformly" rather than "spawn nothing". Missing entries default to weight 1;
        /// negative weights clamp to 0 (never selected).
        /// </summary>
        public static float[] BuildWeightCumulative(int count, float[] weights)
        {
            if (count <= 0) return null;
            if (weights == null || weights.Length == 0) return null; // uniform

            var cumulative = new float[count];
            float running = 0f;
            for (int i = 0; i < count; i++)
            {
                float w = i < weights.Length ? weights[i] : 1f;
                if (w < 0f) w = 0f;
                running += w;
                cumulative[i] = running;
            }
            return running > 0f ? cumulative : null;
        }

        /// <summary>Picks an index, weighted if a cumulative table is supplied, else uniform.</summary>
        public static int PickIndex(float[] cumulative, int count, System.Random rng)
        {
            if (count <= 0) return -1;
            if (cumulative == null) return rng.Next(count);
            float total = cumulative[cumulative.Length - 1];
            return UpperBound(cumulative, (float)rng.NextDouble() * total);
        }

        /// <summary>First index whose cumulative value is greater than or equal to target.</summary>
        public static int LowerBound(float[] cumulative, float target)
        {
            int lo = 0, hi = cumulative.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (cumulative[mid] < target) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// First index whose cumulative value is STRICTLY greater than target. Used for
        /// weighted selection so zero-weight entries — whose cumulative equals the previous
        /// entry's — are unreachable. A lower bound would occasionally land on them.
        /// </summary>
        public static int UpperBound(float[] cumulative, float target)
        {
            int lo = 0, hi = cumulative.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (cumulative[mid] <= target) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }
}