using System.Collections.Generic;
using Mapbox.BaseModule.Data.Tiles;
using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.GameObjectModifiers
{
    public enum ExclusionGeometryType
    {
        Polygon, // water, building — captured as a triangulated mesh; reject points inside any triangle
        Line     // road — captured as polylines; reject points within a buffer distance of the line
    }

    /// <summary>
    /// Per-tile store of "do not scatter here" geometry, in normalized tile space
    /// (x in [0,1], z in [0,-1]) — the same space the park scatter sampler works in.
    ///
    /// Polygons are stored as TRIANGULATED meshes (vertices + triangle indices), captured
    /// straight from the SDK's Polygon Mesh Modifier output. This means the SDK's own
    /// triangulator has already handled multiple polygons and holes correctly, so the
    /// scatter test reduces to a simple point-in-triangle check with no winding ambiguity.
    ///
    /// Lines (roads) are stored as polylines and tested by distance.
    ///
    /// Capture mesh-modifiers (background mesh-gen phase) WRITE here; the scatter
    /// GameObject-modifier (later, main thread) READS here. All mesh modifiers for a tile
    /// run before any GameObject modifier for that tile, so data is ready before scatter.
    /// Access is locked because writes are on task threads and reads on the main thread.
    ///
    /// NOTE (flower field): a persistent, player-centric consumer such as FlowerFieldManager
    /// reads tiles at ARBITRARY times — including while capture for a neighbouring tile is
    /// still running on a background thread. Such consumers MUST use TryGetSnapshot(), not
    /// Get(), because iterating the live lists races with AddMesh/AddLines.
    /// </summary>
    public static class ExclusionRegistry
    {
        /// <summary>A flat triangle soup on the x/z plane, with an x/z bounding box for early-out.</summary>
        public class ExclusionMesh
        {
            public readonly List<Vector3> Verts;
            public readonly List<int> Tris;
            public readonly float MinX, MinZ, MaxX, MaxZ;

            public ExclusionMesh(List<Vector3> verts, List<int> tris)
            {
                Verts = verts;
                Tris = tris;
                float minX = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxZ = float.MinValue;
                for (int i = 0; i < verts.Count; i++)
                {
                    var v = verts[i];
                    if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x;
                    if (v.z < minZ) minZ = v.z; if (v.z > maxZ) maxZ = v.z;
                }
                MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ;
            }
        }

        /// <summary>A polyline on the x/z plane, with an x/z bounding box for early-out.</summary>
        public class ExclusionLine
        {
            public readonly List<Vector3> Points;
            public readonly float MinX, MinZ, MaxX, MaxZ;

            public ExclusionLine(List<Vector3> points)
            {
                Points = points;
                float minX = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxZ = float.MinValue;
                for (int i = 0; i < points.Count; i++)
                {
                    var v = points[i];
                    if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x;
                    if (v.z < minZ) minZ = v.z; if (v.z > maxZ) maxZ = v.z;
                }
                MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ;
            }
        }

        public class TileExclusions
        {
            public readonly List<ExclusionMesh> Meshes = new(); // water, buildings
            public readonly List<ExclusionLine> Lines = new();   // roads

            /// <summary>
            /// Bumped on every write. Consumers that CACHE a decision made from this tile's
            /// geometry (the flower field marks a cell populated forever) record the version
            /// they used and re-check it, so a decision taken while capture was still in
            /// flight — or before a tile reloaded — gets redone instead of standing wrong.
            /// A tile with no entry reads as version 0, which is distinct from any real one.
            /// </summary>
            public int Version;
        }

        private static readonly Dictionary<CanonicalTileId, TileExclusions> _data = new();

        // Survives entry removal. current version == 0 with lifetime > 0 is the signature of
        // an entry that EXISTED AND WAS DELETED — completely different failure from one that
        // was never written, and indistinguishable without this.
        private static readonly Dictionary<CanonicalTileId, int> _lifetimeWrites = new();
        private static readonly object _lock = new();

        /// <summary>When true, every write and removal is logged with its tile id.</summary>
        public static bool LogWrites = false;

        public static void AddMesh(CanonicalTileId tileId, List<Vector3> verts, List<int> tris)
        {
            if (verts == null || tris == null || tris.Count < 3) return;
            lock (_lock)
            {
                if (!_data.TryGetValue(tileId, out var ex))
                {
                    ex = new TileExclusions();
                    _data[tileId] = ex;
                }
                ex.Meshes.Add(new ExclusionMesh(verts, tris));
                ex.Version++;
                _lifetimeWrites.TryGetValue(tileId, out var lw);
                _lifetimeWrites[tileId] = lw + 1;
                if (LogWrites)
                    Debug.Log($"[ExclusionRegistry] +mesh {tileId.Z}/{tileId.X}/{tileId.Y} " +
                              $"(meshes={ex.Meshes.Count} lines={ex.Lines.Count} v{ex.Version})");
            }
        }

        public static void AddLines(CanonicalTileId tileId, List<List<Vector3>> lines)
        {
            if (lines == null || lines.Count == 0) return;
            lock (_lock)
            {
                if (!_data.TryGetValue(tileId, out var ex))
                {
                    ex = new TileExclusions();
                    _data[tileId] = ex;
                }
                foreach (var line in lines)
                    if (line != null && line.Count >= 2)
                        ex.Lines.Add(new ExclusionLine(line));
                ex.Version++;
                _lifetimeWrites.TryGetValue(tileId, out var lw);
                _lifetimeWrites[tileId] = lw + 1;
                if (LogWrites)
                    Debug.Log($"[ExclusionRegistry] +lines {tileId.Z}/{tileId.X}/{tileId.Y} " +
                              $"(meshes={ex.Meshes.Count} lines={ex.Lines.Count} v{ex.Version})");
            }
        }

        /// <summary>Returns the tile's exclusions, or null if nothing was captured.</summary>
        public static TileExclusions Get(CanonicalTileId tileId)
        {
            lock (_lock)
            {
                return _data.TryGetValue(tileId, out var ex) ? ex : null;
            }
        }

        /// <summary>
        /// Copies this tile's exclusion geometry into the supplied lists UNDER THE LOCK.
        /// Use this instead of Get() from any consumer that reads at an arbitrary time
        /// (i.e. not immediately after the tile's own mesh-gen phase), because capture may
        /// still be appending on a background thread and iterating the live lists would
        /// throw. The ExclusionMesh / ExclusionLine objects are immutable once constructed,
        /// so copying the outer references is sufficient — no deep copy needed.
        /// Returns false if the tile has no captured geometry at all.
        /// </summary>
        public static bool TryGetSnapshot(CanonicalTileId tileId,
                                          List<ExclusionMesh> meshes,
                                          List<ExclusionLine> lines)
        {
            return TryGetSnapshot(tileId, meshes, lines, out _);
        }

        /// <summary>Snapshot plus the version it was taken at, for cache validation.</summary>
        public static bool TryGetSnapshot(CanonicalTileId tileId,
                                          List<ExclusionMesh> meshes,
                                          List<ExclusionLine> lines,
                                          out int version)
        {
            meshes.Clear();
            lines.Clear();
            lock (_lock)
            {
                if (!_data.TryGetValue(tileId, out var ex)) { version = 0; return false; }
                meshes.AddRange(ex.Meshes);
                lines.AddRange(ex.Lines);
                version = ex.Version;
            }
            return true;
        }

        /// <summary>Current version for a tile, or 0 if nothing has been captured for it.</summary>
        public static int GetVersion(CanonicalTileId tileId)
        {
            lock (_lock)
            {
                return _data.TryGetValue(tileId, out var ex) ? ex.Version : 0;
            }
        }

        /// <summary>
        /// Snapshot of every tile id currently held. Used by distance-based pruning so the
        /// registry doesn't grow unbounded once entry lifetime is no longer owned by the
        /// park scatter modifier's Finalize.
        /// </summary>
        public static void GetTileIds(List<CanonicalTileId> into)
        {
            into.Clear();
            lock (_lock)
            {
                foreach (var kvp in _data)
                    into.Add(kvp.Key);
            }
        }

        // NOTE: the old public Clear(CanonicalTileId) is GONE, deliberately. An earlier
        // version of ScatterPrefabModifier.Finalize cleared a tile's entry whenever a PARK
        // entity unloaded — correct when the park scatter was the registry's only reader,
        // catastrophic now: the vector module serves re-requested tiles from its own cache
        // WITHOUT re-running mesh modifiers, so a cleared entry is never rebuilt and every
        // later consumer of that tile spawns unrestricted. Whole tiles, deterministic from a
        // fixed start (startup LOD churn is deterministic). If this rename produces a compile
        // error in ScatterPrefabModifier.Finalize, that stale file IS the bug — install the
        // refactored ScatterPrefabModifier.cs, whose Finalize does not touch the registry.
        /// <summary>Removal for distance-based pruning ONLY. Never call per-entity.</summary>
        public static void RemoveForPrune(CanonicalTileId tileId)
        {
            lock (_lock)
            {
                if (_data.Remove(tileId) && LogWrites)
                    Debug.Log($"[ExclusionRegistry] PRUNED {tileId.Z}/{tileId.X}/{tileId.Y}");
            }
        }

        /// <summary>Total writes ever made for this tile, surviving removal. 0 = never captured.</summary>
        public static int GetLifetimeWrites(CanonicalTileId tileId)
        {
            lock (_lock)
            {
                return _lifetimeWrites.TryGetValue(tileId, out var n) ? n : 0;
            }
        }

        public static void ClearAll()
        {
            lock (_lock)
            {
                _data.Clear();
                _lifetimeWrites.Clear();
            }
        }
    }
}