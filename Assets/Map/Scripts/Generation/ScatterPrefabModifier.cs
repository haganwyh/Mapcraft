using System;
using System.Collections.Generic;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Unity;
using Mapbox.BaseModule.Utilities;
using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.GameObjectModifiers
{
    [Serializable]
    public class ScatterPrefabModifierSettings
    {
        [Tooltip("Prefabs to scatter. One is chosen per point (e.g. trees + flowers), by weighted " +
                 "random if PrefabWeights is set, otherwise uniformly.")]
        public GameObject[] Prefabs;

        [Tooltip("Optional relative spawn chance per prefab, paired by index with Prefabs. " +
                 "Higher = more common (e.g. 3 vs 1 means roughly 3x as likely). Values are relative, " +
                 "not percentages — [3,1,1] and [30,10,10] behave the same. Leave EMPTY for equal " +
                 "chance. If non-empty it should have the same length as Prefabs; missing entries " +
                 "default to weight 1, and a prefab with weight <= 0 is never spawned.")]
        public float[] PrefabWeights;

        [Header("Tile Zoom Gate")]
        [Tooltip("Only scatter on tiles whose own zoom level is >= this. Rejects GENERATION " +
                 "(not just visibility) per-tile. IMPORTANT: this is the TILE's data level, which " +
                 "is capped by the Vector module's 'Clamp Data Level to Max'. If clamp = 16, no " +
                 "tile ever exceeds z16, so setting this above 16 yields ZERO objects. To gate at " +
                 "the deepest/nearest tiles with clamp=16, set Min=16. To distinguish z17/z18, raise " +
                 "the clamp first.")]
        public int MinTileZoom = 16;

        [Tooltip("Only scatter on tiles whose own zoom level is <= this. Also capped by the module's " +
                 "Clamp Data Level to Max. Leave high (e.g. 22) unless you want to exclude the deepest tiles.")]
        public int MaxTileZoom = 22;

        [Tooltip("Instances per unit of local mesh surface area. Count = area x this, so density " +
                 "is CONSTANT across parks of any size (a park twice as big gets twice as many). " +
                 "Local areas are small numbers, so useful values are typically in the tens to hundreds. " +
                 "Tune by eye.")]
        public float DensityFactor = 100f;

        [Tooltip("Optional hard cap on instances per park, so one very large park can't spawn thousands. " +
                 "Set to 0 for no cap. Note: a cap breaks perfectly constant density on parks that hit it.")]
        public int MaxPerFeature = 200;

        [Tooltip("Hide the park ground mesh (scatter still uses it as a sampling surface).")]
        public bool HideSourceMesh = true;

        [Tooltip("Uniform scale applied to each instance (prefab's own size x this).")]
        public float ScaleMultiplier = 1f;

        [Tooltip("Random uniform scale variation, e.g. 0.2 = +/-20%, so instances don't look cloned.")]
        [Range(0f, 0.9f)] public float ScaleJitter = 0.2f;

        [Tooltip("Randomize Y rotation per instance.")]
        public bool RandomYRotation = true;

        [Tooltip("Deterministic per-feature seed so a park scatters the same way across reloads.")]
        public bool DeterministicPerFeature = true;

        [Header("Exclusion (avoid water / roads / buildings)")]
        [Tooltip("If on, reject scatter points that fall inside captured polygon exclusions " +
                 "(water/buildings) or within RoadBuffer of captured line exclusions (roads). " +
                 "Requires Exclusion Capture Mesh Modifiers on those layers' stacks.")]
        public bool UseExclusions = true;

        [Tooltip("Distance (in normalized tile units) to keep clear of road centerlines. " +
                 "Tile span is ~1 unit, so values are small, e.g. 0.01–0.05. Larger = wider road clearance.")]
        public float RoadBuffer = 0.02f;

        [Tooltip("Extra clearance (in normalized tile units) around water/building areas, so objects " +
                 "don't spawn right at the edge. Effectively grows the excluded area by this much. " +
                 "Tile span is ~1 unit, so use small values, e.g. 0.005–0.03. 0 = exclude only the exact area.")]
        public float ExclusionMargin = 0.01f;

        [Tooltip("Extra attempts per needed instance when exclusions reject points, before giving up. " +
                 "Higher = denser parks even when much area is excluded, at more CPU cost.")]
        public int ExclusionRetryMultiplier = 6;

        [Header("Performance")]
        [Tooltip("How many candidate points to sample AND exclusion-test per frame, across ALL " +
                 "parks combined. The exclusion scan (especially roads) is the main CPU cost; " +
                 "spreading it over frames removes the tile-load hitch. Lower = smoother but slower " +
                 "fill (e.g. 80–150 for mobile); higher = faster, bigger per-frame cost.")]
        public int ExclusionTestsPerFrame = 120;

        [Tooltip("How many tree instances to actually create per frame, across ALL parks combined. " +
                 "Instantiation is the other main cost; spreading it over frames removes the spike " +
                 "so trees pop in over a moment instead of hitching. Lower = smoother but slower fill " +
                 "(e.g. 20–40 for mobile); higher = faster fill, bigger per-frame cost.")]
        public int SpawnsPerFrame = 40;
    }

    [Serializable]
    public class ScatterPrefabModifier : GameObjectModifier
    {
        public Action<GameObject> PrefabCreated = (s) => { };

        private readonly UnityContext _unityContext;
        private readonly ScatterPrefabModifierSettings _settings;
        private readonly Dictionary<VectorEntity, DeferredSpawner.ScatterBatch> _objects = new();

        public ScatterPrefabModifier(UnityContext unityContext, ScatterPrefabModifierSettings settings)
        {
            _unityContext = unityContext;
            _settings = settings;
        }

        public override void Run(VectorEntity ve, IMapInformation mapInformation)
        {
            if (_settings.Prefabs == null || _settings.Prefabs.Length == 0)
                return;
            if (ve == null || ve.Transform == null)
                return;
            if (_objects.ContainsKey(ve))
                return;

            // Per-tile zoom gate. Unlike the stack's Visibility Zoom Range (which only
            // toggles SetActive by CAMERA zoom on already-generated objects), this rejects
            // generation for tiles whose own level is outside [MinTileZoom, MaxTileZoom].
            // Lets landuse scatter on a tighter zoom range than the shared Vector module's
            // Reject Tiles Outside Zoom (e.g. trees only at z17–18 even if buildings go to z15).
            int tileZoom = ve.Feature.TileId.Z;
            if (tileZoom < _settings.MinTileZoom || tileZoom > _settings.MaxTileZoom)
                return;

            var mesh = ResolveMesh(ve);
            if (mesh == null || mesh.vertexCount == 0)
                return;

            var verts = mesh.vertices; // local space — same space the spawned children use
            var tris = mesh.triangles;
            if (tris.Length < 3)
                return;

            // Build a per-triangle cumulative-area table so we can pick triangles weighted
            // by their area (uniform sampling over the whole surface, not per-triangle).
            int triCount = tris.Length / 3;
            var cumulative = new float[triCount];
            float totalArea = 0f;
            for (int t = 0; t < triCount; t++)
            {
                var a = verts[tris[t * 3 + 0]];
                var b = verts[tris[t * 3 + 1]];
                var c = verts[tris[t * 3 + 2]];
                float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                if (float.IsNaN(area) || float.IsInfinity(area)) area = 0f;
                totalArea += area;
                cumulative[t] = totalArea;
            }
            if (totalArea <= 0f)
                return;

            // CONSTANT DENSITY: instance count is directly proportional to surface area.
            // A park twice as large gets twice as many instances.
            int target = Mathf.RoundToInt(totalArea * _settings.DensityFactor);
            if (_settings.MaxPerFeature > 0)
                target = Mathf.Min(target, _settings.MaxPerFeature);
            if (target <= 0)
            {
                HideMesh(ve);
                _objects.Add(ve, new DeferredSpawner.ScatterBatch());
                return;
            }

            var rng = _settings.DeterministicPerFeature
                ? new System.Random(unchecked((int)ve.Feature.Data.Id))
                : new System.Random();

            // Pull this tile's exclusion geometry (water/building polygons, road lines),
            // captured by Exclusion Capture Mesh Modifiers earlier in the same tile's
            // mesh-gen phase. Null if exclusions disabled or nothing was captured.
            //
            // Take() copies the registry's lists under its lock rather than handing out the
            // live object. The task below outlives this frame, and a tile that unloads and
            // reloads appends into that live object from a background thread — which would
            // mutate the lists while the deferred sampler is walking them.
            var exclusions = _settings.UseExclusions
                ? ExclusionSampling.Take(ve.Feature.TileId)
                : null;

            // With exclusions, some sampled points are rejected, so allow extra attempts
            // to still reach the target count. Without exclusions, attempts == target.
            int maxAttempts = exclusions != null
                ? target * Mathf.Max(1, _settings.ExclusionRetryMultiplier)
                : target;

            // Hand everything to the deferred spawner. The expensive work — exclusion
            // testing AND instantiation — happens later, spread across frames under two
            // independent per-frame budgets. Run() returns almost immediately.
            var spawner = DeferredSpawner.Instance;
            spawner.SetBudgets(_settings.ExclusionTestsPerFrame, _settings.SpawnsPerFrame);

            var batch = new DeferredSpawner.ScatterBatch { OnSpawned = PrefabCreated };

            // Build a cumulative weight table for weighted prefab selection. Null if weights
            // aren't configured, in which case the spawner falls back to uniform pick. Done
            // once here (cheap) rather than per-point.
            float[] prefabCumulative = ExclusionSampling.BuildWeightCumulative(
                _settings.Prefabs.Length, _settings.PrefabWeights);

            spawner.EnqueueTask(new DeferredSpawner.ScatterTask
            {
                Verts = verts,
                Tris = tris,
                Cumulative = cumulative,
                TotalArea = totalArea,
                Parent = ve.Transform,
                Prefabs = _settings.Prefabs,
                PrefabCumulative = prefabCumulative,
                ScaleMultiplier = _settings.ScaleMultiplier,
                ScaleJitter = _settings.ScaleJitter,
                RandomYRotation = _settings.RandomYRotation,
                Exclusions = exclusions,
                MarginSq = _settings.ExclusionMargin * _settings.ExclusionMargin,
                RoadBufferSq = _settings.RoadBuffer * _settings.RoadBuffer,
                Rng = rng,
                Target = target,
                MaxAttempts = maxAttempts,
                Batch = batch
            });

            HideMesh(ve);
            _objects.Add(ve, batch);
        }

        public override void Finalize(VectorEntity entity)
        {
            base.Finalize(entity);
            if (_objects.TryGetValue(entity, out var batch))
            {
                // Cancel so any still-queued spawn jobs for this park are skipped.
                batch.Cancelled = true;
                // Destroy whatever already spawned.
                var list = batch.Spawned;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null)
                        GameObject.Destroy(list[i]);
                list.Clear();
                _objects.Remove(entity);
            }

            // NOTE: this used to clear the tile's captured exclusion geometry here. That is
            // wrong once anything other than the park scatter reads the registry — a tile can
            // carry roads and water but no park at all, and a park entity can be finalized
            // while its tile is still loaded. Registry lifetime is now owned by
            // FlowerFieldManager.PruneRegistry (distance-based), which makes no assumptions
            // about any one layer's entity lifecycle.
        }

        // The SDK may expose the generated mesh on ve.Mesh or on the MeshFilter; try both.
        private static Mesh ResolveMesh(VectorEntity ve)
        {
            if (ve.Mesh != null && ve.Mesh.vertexCount > 0)
                return ve.Mesh;
            if (ve.MeshFilter != null)
                return ve.MeshFilter.sharedMesh != null ? ve.MeshFilter.sharedMesh : ve.MeshFilter.mesh;
            return null;
        }

        private void HideMesh(VectorEntity ve)
        {
            if (_settings.HideSourceMesh && ve.MeshRenderer != null)
                ve.MeshRenderer.enabled = false;
        }
    }
}