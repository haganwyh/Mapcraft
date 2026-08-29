using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Utilities;
using Mapbox.Example.Scripts.Map;
using Mapbox.VectorModule.MeshGeneration.GameObjectModifiers;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mapbox.Flowers
{
    public enum FlowerRenderMode
    {
        /// <summary>One pooled GameObject per flower. Colliders, scripts and per-flower
        /// inspection work; costs a Transform, a MeshRenderer and a culling entry each.</summary>
        GameObjects,

        /// <summary>No GameObjects at all — matrices submitted through Graphics.RenderMeshInstanced.
        /// A few draw calls for the whole field. No colliders, nothing in the hierarchy.</summary>
        GpuInstanced
    }

    /// <summary>
    /// Scatters flower prefabs on the ground in a radius around the player, avoiding water,
    /// roads and buildings by reading the same ExclusionRegistry the park tree scatter uses.
    ///
    /// THIS IS A FULL REWRITE OF FlowerFieldManager, AS A NEW COMPONENT CLASS. The rename is
    /// deliberate: several correctness-critical values on the old component had rotted through
    /// Unity's stale-serialization behaviour (a field added to a component that already exists
    /// in a scene deserializes to the TYPE DEFAULT, not the C# initializer — the exact trap
    /// documented in this project's own reference notes). A fresh class guarantees a fresh
    /// serialized state. Remove the old FlowerFieldManager component and its script; add this.
    ///
    /// DESIGN RECAP
    /// ------------
    /// * The world is divided into cells by subdividing each DataZoom tile. Cells are the unit
    ///   of spawn/despawn; each is seeded from (tile, cx, cy), so walking away and back
    ///   regenerates identical flowers.
    /// * All flowers live in ANCHORED space under a single root whose localPosition tracks the
    ///   anchor tile corner: map movement costs one conversion per frame, not one per flower.
    /// * Exclusion geometry is read from ExclusionRegistry as the UNION of the entries at the
    ///   cell's DataZoom tile AND its parents down to z(DataZoom-3). The module legitimately
    ///   loads the same ground at several zooms over a session, and which layers' capture
    ///   landed at which level depends on load order — so no single level can be trusted to
    ///   hold everything. The search depth is a CONSTANT, not an inspector field, precisely
    ///   because it once was a field and deserialized to 0 on an existing component, silently
    ///   disabling the parent search while everything looked correct.
    /// * Cells record the chain version-sum they decided on; data arriving later at any level
    ///   invalidates and repopulates them.
    /// * FAIL-CLOSED: a cell whose grace expires with no data anywhere spawns NOTHING rather
    ///   than spawning unrestricted. Missing flowers in a genuinely-empty rural tile are a
    ///   mild artifact; flowers across a harbour are a bug. Version invalidation still fills
    ///   the cell the moment real data arrives.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class FlowerField : MonoBehaviour
    {
        /// <summary>
        /// Parent zoom levels searched below DataZoom (z16 + z15 + z14 + z13). A constant, not
        /// an inspector field — see the class comment for why. Cost: four dictionary lookups
        /// per cell, cached per refresh tick.
        /// </summary>
        private const int ParentSearchLevels = 3;

        // Re-anchor once the root drifts this far from the map centre; flower positions are
        // (large anchor offset) + (small local offset) and float precision degrades as the two
        // grow and cancel. ~18 km of travel; the shift is cheap and rare.
        private const float ReanchorDistance = 20000f;

        #region Inspector

        [Header("Wiring")]
        [Tooltip("The GameObject carrying MapboxMapBehaviour (or any MapBehaviourCore).")]
        public MapBehaviourCore MapCore;

        [Tooltip("The avatar. On a slippy map this sits at world origin while the map moves " +
                 "underneath; the transform is converted into map-local space each tick rather " +
                 "than assumed to be the map centre.")]
        public Transform Player;

        [Tooltip("Flower prefabs. One is chosen per point, weighted by PrefabWeights if set. " +
                 "The renderer does NOT need to be on the prefab root — child offsets, FBX " +
                 "import rotations and LODGroups (LOD0 only) are handled.")]
        public GameObject[] Prefabs;

        [Tooltip("Optional relative spawn chance per prefab, paired by index with Prefabs. " +
                 "Relative not percentage; empty = uniform; weight <= 0 = never spawned; " +
                 "missing entries default to 1.")]
        public float[] PrefabWeights;

        [Header("Field")]
        [Tooltip("Flowers exist within this many METRES of the player — real ground metres; " +
                 "the manager measures the local-units-per-metre ratio at runtime.")]
        public float RadiusMetres = 400f;

        [Tooltip("Extra metres beyond RadiusMetres before a cell despawns, so GPS jitter at " +
                 "the boundary doesn't thrash cells on/off.")]
        public float DespawnHysteresisMetres = 10f;

        [Tooltip("Flowers per square metre of ground. 0.005 at a 400 m radius is ~2,500 flowers.")]
        public float FlowersPerSquareMetre = 0.005f;

        [Tooltip("Subdivisions per tile axis; sets the cell size. At z16 a tile is ~611 local " +
                 "units, so 32 gives ~18 m cells — the right granularity for a 400 m field. " +
                 "Raise only for small radii (128 suits ~40 m).")]
        [Range(8, 256)] public int CellsPerTileAxis = 32;

        [Tooltip("MUST equal the Vector module's 'Clamp Data Level to Max'. The cell grid and " +
                 "the exclusion lookups are aligned to tiles of this zoom.")]
        public int DataZoom = 16;

        [Tooltip("Global seed. Change to reshuffle the whole world's flower layout.")]
        public int RandomSeed = 1337;

        [Header("Exclusion")]
        [Tooltip("Reject points inside captured polygon exclusions (water/buildings) or within " +
                 "RoadBuffer of captured road lines. Requires Exclusion Capture Mesh Modifiers " +
                 "on those layers' stacks — the same assets the tree scatter uses. Their " +
                 "MinTileZoom/MaxTileZoom should be fully open (0 / 22): the module's own " +
                 "'Reject Tiles Outside Zoom' already bounds the work, and a capture gate " +
                 "tighter than the tiles the module actually loads is precisely how tiles end " +
                 "up with no exclusion data.")]
        public bool UseExclusions = true;

        [Tooltip("Clearance from road centrelines, in NORMALIZED TILE UNITS (tile span ~1).")]
        public float RoadBuffer = 0.025f;

        [Tooltip("Extra clearance around water/building polygons, in normalized tile units.")]
        public float ExclusionMargin = 0.01f;

        [Tooltip("Defer cells whose tile has no registry data yet, instead of populating " +
                 "immediately — 'no entry' usually means capture hasn't finished.")]
        public bool RequireExclusionData = true;

        [Tooltip("How long to keep deferring a tile with no registry entry anywhere in its " +
                 "chain before treating it as genuinely empty. Generous is safe: cells are " +
                 "re-validated when data arrives, so waiting costs only a later fill.")]
        public float ExclusionGraceSeconds = 8f;

        [Tooltip("How long a tile's chain must stop CHANGING before it is trusted; guards " +
                 "against reading a tile that has captured its roads but not yet its water.")]
        public float ExclusionSettleSeconds = 0.5f;

        [Tooltip("SAFETY LATCH. When on, a cell whose grace expires with NO data at any level " +
                 "spawns NOTHING instead of spawning unrestricted. Missing flowers in a rural " +
                 "field are a mild artifact; flowers across a harbour are a bug report. The " +
                 "cell still fills automatically if capture data arrives later.")]
        public bool FailClosed = true;

        [Tooltip("Registry entries farther than this many z16-equivalent tiles from the map " +
                 "centre are dropped. Keep generous: the vector module serves revisited tiles " +
                 "from its own cache WITHOUT re-running mesh modifiers, so a pruned entry for " +
                 "a cached tile is gone for good. 16 tiles is ~9 km, a few MB worst case.")]
        public int PruneRadiusTiles = 16;

        [Header("Placement")]
        [Tooltip("Uniform scale applied to each instance, multiplying the prefab's own scale.")]
        public float ScaleMultiplier = 7f;

        [Tooltip("Random uniform scale variation, e.g. 0.25 = +/-25%.")]
        [Range(0f, 0.9f)] public float ScaleJitter = 0.25f;

        public bool RandomYRotation = true;

        [Tooltip("If the prefab is authored at real-world size, divide by the measured " +
                 "metres-per-local-unit so it renders at true size.")]
        public bool PrefabSizeIsMetres = false;

        [Tooltip("Sample terrain elevation per flower at spawn. Leave off for a flat map.")]
        public bool SnapToTerrain = false;

        [Tooltip("Constant vertical offset, e.g. to clear a z-fighting ground plane.")]
        public float YOffset = 0f;

        [Tooltip("Candidate points sampled per flower wanted before giving up on a cell. " +
                 "Higher keeps density up in cells that are mostly road/building.")]
        [Range(1, 16)] public int AttemptsPerFlower = 3;

        [Header("Natural Distribution")]
        // ALL of these default to 0 = OFF, deliberately: this component already exists in
        // scenes, and fields added to an existing component deserialize to the TYPE DEFAULT,
        // not the C# initializer. With 0 meaning "old behaviour", stale and fresh components
        // agree, and turning the look on is an explicit Inspector action. Recommended values
        // are in each tooltip.
        [Tooltip("Metres per density-patch feature. Modulates flower density with smooth " +
                 "noise: drifts of dense flowers with sparse or bare meadow between them, " +
                 "instead of the same density everywhere. 0 = off. Recommended: 30.")]
        public float DensityPatchScaleMetres = 0f;

        [Tooltip("How strongly density varies, 0-1. Higher = more area fully bare and " +
                 "flowers concentrated into the drifts. The overall flower count is roughly " +
                 "preserved (density inside drifts is boosted to compensate). Recommended: 0.65.")]
        [Range(0f, 0.95f)] public float DensityPatchContrast = 0f;

        [Tooltip("Metres per species-patch feature. Each patch GROUP gets its own noise field " +
                 "and dominates where its field is high — drifts of red here, blue there, " +
                 "mixing at the borders — instead of independent per-flower picks " +
                 "(salt-and-pepper). 0 = off. Recommended: 18.")]
        public float SpeciesPatchScaleMetres = 0f;

        [Tooltip("Patch-group id per prefab, paired by index with Prefabs. Prefabs sharing an " +
                 "id form ONE species for patching: they share a noise field, their weights " +
                 "pool, and a clump picks among them by weight — so with 8 yellow variants " +
                 "all marked 0, a yellow drift contains all 8 mixed, rather than 8 rival " +
                 "yellow sub-patches. Grouping is also what makes equal-weight colours come " +
                 "out equal: with per-prefab fields, each colour's total rides the regional " +
                 "luck of its 8 private noise fields. Example for 40 prefabs in 5 colours of " +
                 "8: [0,0,0,0,0,0,0,0, 1,1,1,1,1,1,1,1, ...]. Empty = every prefab is its " +
                 "own group (old behaviour).")]
        public int[] PrefabPatchGroups;

        [Tooltip("How hard the species patches separate. 1 = gentle bias, 4+ = nearly " +
                 "single-species drifts. Recommended: 3.")]
        [Range(0f, 6f)] public float SpeciesPatchSharpness = 0f;

        [Tooltip("Average flowers per clump. Flowers spawn in small same-species clusters " +
                 "instead of lone points — the micro-texture that most makes a field read as " +
                 "natural. 0 or 1 = off. Recommended: 4.")]
        [Range(0, 12)] public int ClumpSize = 0;

        [Tooltip("Clump radius in metres — how far clump members spread around their centre, " +
                 "denser toward it. Recommended: 1.2.")]
        public float ClumpRadiusMetres = 0f;

        [Tooltip("Fraction of the radius where the edge fade begins; visible density then " +
                 "falls smoothly to zero at the radius, so the field has a soft organic " +
                 "boundary instead of a hard generated-looking disc. Implemented as per-flower " +
                 "VISIBILITY (each flower has a persistent rank tested against the falloff at " +
                 "its current distance), so flowers materialize ahead and dissolve behind as " +
                 "the player walks — and cells fade out before they despawn, hiding the " +
                 "despawn pop. 0 = off. Recommended: 0.45.")]
        [Range(0f, 0.95f)] public float EdgeFadeFraction = 0f;

        [Header("Performance")]
        [Tooltip("Seconds between recomputes of which cells should exist. 0.5 is plenty at " +
                 "walking pace with ~18 m cells.")]
        public float UpdateInterval = 0.5f;

        [Tooltip("Cells populated per frame (coarse budget).")]
        public int CellsPerFrame = 60;

        [Tooltip("Exclusion tests per frame across all cells (fine budget; the road scan " +
                 "dominates — lower this first if a fill hitches).")]
        public int ExclusionTestsPerFrame = 150;

        [Tooltip("Spawns per frame. In GpuInstanced mode a 'spawn' is a struct append and " +
                 "this can be high; in GameObjects mode it bounds Instantiate/pool churn.")]
        public int SpawnsPerFrame = 60;

        [Tooltip("Absolute cap on live flowers. Set ABOVE pi x Radius^2 x density or the " +
                 "field stops short. 0 = uncapped.")]
        public int MaxLiveFlowers = 3000;

        [Header("Rendering")]
        [Tooltip("GpuInstanced draws the whole field as a handful of instanced draw calls " +
                 "with zero GameObjects. Use GameObjects only if flowers need colliders, " +
                 "scripts, or hierarchy presence. Materials need 'Enable GPU Instancing'.")]
        public FlowerRenderMode RenderMode = FlowerRenderMode.GpuInstanced;

        [Tooltip("Thousands of tiny shadow casters is one of the most expensive things a " +
                 "mobile GPU can do, for a near-invisible result at map camera distance.")]
        public bool CastShadows = false;

        public bool ReceiveShadows = false;

        [Header("Debug")]
        public bool DebugGizmos = true;

        [Tooltip("Logs live/pending/queued counts and the measured calibration once per second.")]
        public bool LogStats = false;

        [Tooltip("Logs every registry write and removal with its tile id.")]
        public bool LogRegistryWrites = false;

        #endregion

        #region Internal types

        private class Instance
        {
            public GameObject Go;      // null in GpuInstanced mode
            public int PrefabIndex;
            public Matrix4x4 Local;    // anchored-space TRS (GpuInstanced mode)
            public float Rank;         // persistent [0,1); flower visible while Rank < falloff
        }

        private class Cell
        {
            public long Key;
            public int Gx, Gy;                    // GLOBAL cell coords at DataZoom (unwrapped)
            public CanonicalTileId SourceTileId;  // the DataZoom tile this cell sits in
            public int ExclusionVersion;          // chain version-sum the decision was made at
            public int Generation;                // unique per POPULATION of this key — see SpawnJob
            public readonly List<Instance> Instances = new();
        }

        private struct PendingCell
        {
            public long Key;
            public int Gx, Gy;
        }

        private struct SpawnJob
        {
            public long CellKey;
            public int PrefabIndex;
            public Vector3 LocalPos;              // anchored space
            public Quaternion Rot;
            public float Scale;
            public float Rank;

            // Guards against STALE JOBS. A cell can be populated, invalidated (its tile's
            // exclusion data grew), despawned and REPOPULATED under the same key while jobs
            // from the first population still sit in the spawn queue — jobs whose positions
            // were tested against the incomplete data. Matching on CellKey alone re-attaches
            // those old jobs to the new cell, planting flowers inside water/buildings that
            // then survive forever (the new cell's version is current, so invalidation never
            // fires again). Probabilistic and density-scaled: the fuller the spawn queue, the
            // longer old jobs linger across an invalidation. The generation stamp makes a
            // repopulated cell reject its predecessor's jobs.
            public int Generation;
        }

        /// <summary>
        /// One level of the exclusion chain: a snapshot of a tile's geometry, the transform
        /// from DataZoom tile-01 space into that tile's tile-01 space, and the buffers
        /// pre-scaled into that tile's units (a parent tile is twice as wide per level, so the
        /// same ground clearance is half the normalized distance).
        /// </summary>
        private class ChainSlot
        {
            public readonly ExclusionSampling.Snapshot Snap = new();
            public bool Active;
            public float Scale;
            public float OffsetX, OffsetY;
            public float MarginSq, RoadSq;
        }

        private class TileState
        {
            public int Version;
            public float ChangedAt;
            public float FirstSeen;
        }

        private class InstancedBatch
        {
            public Mesh Mesh;
            public Material Material;
            public int SubMesh;
            public readonly List<Matrix4x4> Matrices = new();
            public readonly List<float> Ranks = new();     // parallel to Matrices
        }

        private struct PrefabPart
        {
            public int BatchIndex;
            public Matrix4x4 Offset;              // renderer transform relative to prefab root
        }

        #endregion

        #region State

        private MapboxMap _map;
        private Transform _root;

        private CanonicalTileId _anchor;
        private bool _anchorSet;
        private float _span = 1f;                 // local units per tile at DataZoom
        private float _metresPerLocalUnit = 1f;   // measured, latitude-dependent

        private float[] _prefabCumulative;
        private float[] _prefabBaseWeights;   // normalized copy of PrefabWeights (missing=1, neg=0)
        private Vector3[] _prefabBaseScale;
        private Quaternion[] _prefabBaseRot;

        // Species-patch group tables, built in BuildPrefabTables. One entry per GROUP:
        // pooled weight, member prefab indices, and a cumulative weight table over members.
        private float[] _groupWeights;
        private int[][] _groupMembers;
        private float[][] _groupMemberCumulative;
        private float[] _speciesScratch;      // sized to the group count — a fixed-size buffer
                                              // here once silently truncated selection to the
                                              // first 32 prefabs, excluding later ones entirely

        private readonly Dictionary<long, Cell> _cells = new();
        // Pending cells, kept sorted NEAREST-FIRST each refresh. Order is load-bearing: when
        // MaxLiveFlowers can't cover the whole field, whichever cells are processed first get
        // the flowers. Near-first makes the shortfall appear as a sparse outer ring — the
        // visually correct place for it — instead of whatever region the scan happened to
        // reach last.
        private readonly List<PendingCell> _pending = new();
        private readonly HashSet<long> _pendingSet = new();
        private readonly Queue<SpawnJob> _spawns = new();
        private readonly Dictionary<int, Stack<GameObject>> _pool = new();

        private readonly Dictionary<CanonicalTileId, TileState> _tileState = new();
        private readonly Dictionary<CanonicalTileId, int> _chainVersionCache = new();
        private ChainSlot[] _chain;

        private List<InstancedBatch> _batches;
        private List<PrefabPart>[] _prefabParts;
        private Matrix4x4[] _scratch = new Matrix4x4[0];
        private bool _batchDirty;

        private readonly List<CanonicalTileId> _tileIdBuffer = new();
        private readonly List<long> _removeBuffer = new();

        private int _liveCount;
        private int _cellGenerationCounter;
        private float _nextRefresh;
        private float _nextLog;
        private bool _capWarned;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (MapCore == null)
            {
                Debug.LogError("[FlowerField] MapCore is not assigned.", this);
                enabled = false;
                return;
            }
            MapCore.Initialized += OnMapInitialized;
        }

        private void OnMapInitialized(MapboxMap map)
        {
            _map = map;
            BuildPrefabTables();
        }

        private void OnDestroy()
        {
            if (MapCore != null) MapCore.Initialized -= OnMapInitialized;
            DespawnAll();
            if (_root != null) Destroy(_root.gameObject);
        }

        private void LateUpdate()
        {
            if (_map == null || _map.Status < InitializationStatus.ReadyForUpdates) return;
            if (Prefabs == null || Prefabs.Length == 0) return;
            if (!EnsureRoot()) return;

            ExclusionRegistry.LogWrites = LogRegistryWrites;

            if (!_anchorSet)
            {
                SetAnchor(_map.MapInformation.LatitudeLongitude);
                if (!_anchorSet) return;
            }

            // One conversion per frame keeps every flower glued to its geographic point while
            // the map centre eases toward each GPS fix.
            UpdateRootPosition();

            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + Mathf.Max(0.02f, UpdateInterval);
                MaybeReanchor();
                RefreshCells();
                PruneRegistry();
            }

            ProcessPending();
            ProcessSpawns();

            if (RenderMode == FlowerRenderMode.GpuInstanced) RenderInstanced();

            if (LogStats && Time.time >= _nextLog)
            {
                _nextLog = Time.time + 1f;
                int batches = 0;
                if (_batches != null)
                    for (int i = 0; i < _batches.Count; i++)
                        if (_batches[i].Matrices.Count > 0) batches++;
                Debug.Log($"[FlowerField] live={_liveCount} cells={_cells.Count} pending={_pending.Count} " +
                          $"queued={_spawns.Count} batches={batches} span={_span:F1} m/unit={_metresPerLocalUnit:F3}");
            }
        }

        #endregion

        #region Setup / calibration

        private void BuildPrefabTables()
        {
            _prefabBaseScale = new Vector3[Prefabs.Length];
            _prefabBaseRot = new Quaternion[Prefabs.Length];
            for (int i = 0; i < Prefabs.Length; i++)
            {
                var t = Prefabs[i] != null ? Prefabs[i].transform : null;
                _prefabBaseScale[i] = t != null ? t.localScale : Vector3.one;
                _prefabBaseRot[i] = t != null ? t.localRotation : Quaternion.identity;
            }

            _prefabCumulative = ExclusionSampling.BuildWeightCumulative(Prefabs.Length, PrefabWeights);

            _prefabBaseWeights = new float[Prefabs.Length];
            for (int i = 0; i < Prefabs.Length; i++)
            {
                float w = (PrefabWeights != null && PrefabWeights.Length > 0)
                    ? (i < PrefabWeights.Length ? Mathf.Max(0f, PrefabWeights[i]) : 1f)
                    : 1f;
                _prefabBaseWeights[i] = w;
            }

            // Resolve each prefab's patch group (missing/absent entries -> a private group),
            // then build per-group pooled weights and member cumulative tables.
            var groupIds = new List<int>();
            var groupOf = new int[Prefabs.Length];
            for (int i = 0; i < Prefabs.Length; i++)
            {
                int id = (PrefabPatchGroups != null && i < PrefabPatchGroups.Length)
                    ? PrefabPatchGroups[i]
                    : int.MinValue + i;           // unique -> own group
                int gi = groupIds.IndexOf(id);
                if (gi < 0) { gi = groupIds.Count; groupIds.Add(id); }
                groupOf[i] = gi;
            }

            int groupCount = groupIds.Count;
            _groupWeights = new float[groupCount];
            var memberLists = new List<int>[groupCount];
            for (int g = 0; g < groupCount; g++) memberLists[g] = new List<int>();
            for (int i = 0; i < Prefabs.Length; i++)
            {
                _groupWeights[groupOf[i]] += _prefabBaseWeights[i];
                memberLists[groupOf[i]].Add(i);
            }

            _groupMembers = new int[groupCount][];
            _groupMemberCumulative = new float[groupCount][];
            for (int g = 0; g < groupCount; g++)
            {
                _groupMembers[g] = memberLists[g].ToArray();
                var cum = new float[_groupMembers[g].Length];
                float running = 0f;
                for (int m = 0; m < cum.Length; m++)
                {
                    running += _prefabBaseWeights[_groupMembers[g][m]];
                    cum[m] = running;
                }
                _groupMemberCumulative[g] = cum;
            }

            _speciesScratch = new float[groupCount];

            // Decompose each prefab into instanceable parts. The renderer does NOT need to be
            // on the root: its transform relative to the root is baked into the part offset —
            // exactly what the GameObject path gets for free from the hierarchy.
            _batches = new List<InstancedBatch>();
            _prefabParts = new List<PrefabPart>[Prefabs.Length];
            for (int i = 0; i < Prefabs.Length; i++)
            {
                _prefabParts[i] = new List<PrefabPart>();
                if (Prefabs[i] == null) continue;

                CollectParts(Prefabs[i], _prefabParts[i]);

                if (_prefabParts[i].Count == 0 && RenderMode == FlowerRenderMode.GpuInstanced)
                    Debug.LogWarning($"[FlowerField] '{Prefabs[i].name}' has no enabled MeshRenderer " +
                                     "anywhere in it, so there is nothing to instance. If it uses a " +
                                     "SkinnedMeshRenderer or a particle system, switch RenderMode to " +
                                     "GameObjects.", this);
            }
            _batchDirty = true;
        }

        private bool EnsureRoot()
        {
            if (_root != null) return true;
            var mapRoot = _map.UnityContext.MapRoot;
            if (mapRoot == null) return false;

            var go = new GameObject("~FlowerFieldRoot");
            _root = go.transform;
            // worldPositionStays:false so the root composes with any transform on the map
            // hierarchy (AR anchor, rotated rig) rather than pinning itself to world space.
            _root.SetParent(mapRoot, false);
            return true;
        }

        private void SetAnchor(LatitudeLongitude ll)
        {
            LatLonToTileXY(ll.Latitude, ll.Longitude, DataZoom, out var tx, out var ty);
            int n = 1 << DataZoom;
            int ax = Mod((int)Math.Floor(tx), n);
            int ay = Mathf.Clamp((int)Math.Floor(ty), 0, n - 1);
            _anchor = new CanonicalTileId(DataZoom, ax, ay);
            _anchorSet = true;
            Calibrate();
        }

        /// <summary>
        /// Measures (never assumes) the two scale factors the field needs: _span, the local
        /// units per DataZoom tile (respects MapInformation.Scale); and _metresPerLocalUnit,
        /// ground metres per local unit AT THIS LATITUDE (Mercator stretches with latitude, so
        /// RadiusMetres means real metres in Hong Kong and Helsinki alike).
        /// </summary>
        private void Calibrate()
        {
            var info = _map.MapInformation;

            TileXYToLatLon(_anchor.X, _anchor.Y, DataZoom, out var lat0, out var lon0);
            TileXYToLatLon(_anchor.X + 1, _anchor.Y, DataZoom, out var lat1, out var lon1);

            var p0 = info.ConvertLatLngToPosition(new LatitudeLongitude(lat0, lon0));
            var p1 = info.ConvertLatLngToPosition(new LatitudeLongitude(lat1, lon1));
            float span = Mathf.Abs(p1.x - p0.x);
            _span = span > 1e-4f ? span : 1f;

            const double dLat = 0.001;            // ~111 m, small enough to stay linear
            var pB = info.ConvertLatLngToPosition(new LatitudeLongitude(lat0 + dLat, lon0));
            float localDist = Vector3.Distance(p0, pB);
            double metres = dLat * 111320.0;
            _metresPerLocalUnit = localDist > 1e-5f ? (float)(metres / localDist) : 1f;
        }

        private void UpdateRootPosition()
        {
            TileXYToLatLon(_anchor.X, _anchor.Y, DataZoom, out var lat, out var lon);
            _root.localPosition = _map.MapInformation.ConvertLatLngToPosition(new LatitudeLongitude(lat, lon));
        }

        private void MaybeReanchor()
        {
            if (_root.localPosition.sqrMagnitude < ReanchorDistance * ReanchorDistance) return;

            var old = _anchor;
            SetAnchor(_map.MapInformation.LatitudeLongitude);

            float dx = (_anchor.X - old.X) * _span;
            float dz = -(_anchor.Y - old.Y) * _span;
            var shift = new Vector3(dx, 0f, dz);
            if (shift.sqrMagnitude <= 0f) return;

            foreach (var cell in _cells.Values)
            {
                for (int i = 0; i < cell.Instances.Count; i++)
                {
                    var inst = cell.Instances[i];
                    if (inst.Go != null)
                        inst.Go.transform.localPosition -= shift;
                    else
                        inst.Local.SetColumn(3, inst.Local.GetColumn(3) - (Vector4)shift);
                }
            }
            _batchDirty = true;

            int queued = _spawns.Count;
            for (int i = 0; i < queued; i++)
            {
                var job = _spawns.Dequeue();
                job.LocalPos -= shift;
                _spawns.Enqueue(job);
            }
        }

        #endregion

        #region Exclusion chain

        /// <summary>
        /// The exclusion identity of a DataZoom tile: the SUM of registry versions at that
        /// tile and its parents down z16..z13. Data arriving at ANY level changes the sum,
        /// which is what cell invalidation keys on.
        /// </summary>
        private int ChainVersion(CanonicalTileId dataTile)
        {
            if (_chainVersionCache.TryGetValue(dataTile, out var cached)) return cached;

            int sum = 0;
            int z = dataTile.Z, tx = dataTile.X, ty = dataTile.Y;
            for (int step = 0; step <= ParentSearchLevels && z >= 1; step++)
            {
                sum += ExclusionRegistry.GetVersion(new CanonicalTileId(z, tx, ty));
                tx >>= 1; ty >>= 1; z--;
            }
            _chainVersionCache[dataTile] = sum;
            return sum;
        }

        /// <summary>
        /// Snapshots every chain level that has data, with the DataZoom→level transform
        /// (ascending one level: parentF = ((childIndex &amp; 1) + childF) / 2) and pre-scaled
        /// buffers. Returns the version-sum at snapshot time.
        /// </summary>
        private int GatherChain(CanonicalTileId dataTile, out bool anyFound)
        {
            if (_chain == null)
            {
                _chain = new ChainSlot[ParentSearchLevels + 1];
                for (int i = 0; i < _chain.Length; i++) _chain[i] = new ChainSlot();
            }
            for (int i = 0; i < _chain.Length; i++) _chain[i].Active = false;

            anyFound = false;
            int sum = 0;
            int z = dataTile.Z, tx = dataTile.X, ty = dataTile.Y;
            float scale = 1f, offX = 0f, offY = 0f;

            for (int step = 0; step <= ParentSearchLevels && z >= 1; step++)
            {
                var slot = _chain[step];
                if (ExclusionSampling.TryTake(new CanonicalTileId(z, tx, ty), slot.Snap, out int version))
                {
                    slot.Active = true;
                    slot.Scale = scale;
                    slot.OffsetX = offX;
                    slot.OffsetY = offY;
                    float m = ExclusionMargin * scale, r = RoadBuffer * scale;
                    slot.MarginSq = m * m;
                    slot.RoadSq = r * r;
                    anyFound = true;
                    sum += version;
                }

                offX = ((tx & 1) + offX) * 0.5f;
                offY = ((ty & 1) + offY) * 0.5f;
                scale *= 0.5f;
                tx >>= 1; ty >>= 1; z--;
            }
            return sum;
        }

        /// <summary>Point (DataZoom tile-01, south-positive fy) against every active level.</summary>
        private bool IsExcludedByChain(float fx, float fy)
        {
            for (int i = 0; i < _chain.Length; i++)
            {
                var slot = _chain[i];
                if (!slot.Active) continue;
                float sx = slot.OffsetX + fx * slot.Scale;
                float sy = slot.OffsetY + fy * slot.Scale;
                // Registry z convention is [0,-1], hence the negation.
                if (ExclusionSampling.IsExcluded(slot.Snap, sx, -sy, slot.MarginSq, slot.RoadSq))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Whether the SDK has generated content covering this DataZoom tile, judged from
        /// MapVisualizer.ActiveTiles. Render tiles can sit at other zooms than the data tile
        /// (Reject Tiles Outside Zoom spans 15-18 while clamp collapses data to 16), so the
        /// tile itself, two parents up, and descendants two levels down are all checked.
        /// </summary>
        private bool IsTileGenerated(CanonicalTileId dataTile)
        {
            // ActiveTiles is keyed by UnwrappedTileId, not CanonicalTileId (the compiler told
            // us so: the UnwrappedTileId ctor is (z, x, y) with an implicit conversion toward
            // Canonical, so the same integer triple addresses the same tile in both).
            var active = _map.MapVisualizer.ActiveTiles;
            if (active == null) return false;

            // Self, then parents (z15, z14).
            int z = dataTile.Z, tx = dataTile.X, ty = dataTile.Y;
            for (int step = 0; step <= 2 && z >= 1; step++)
            {
                if (active.ContainsKey(new UnwrappedTileId(z, tx, ty))) return true;
                tx >>= 1; ty >>= 1; z--;
            }

            // Descendants (z17 children, z18 grandchildren). Only reached while a cell is
            // stuck waiting, so the lookup count is irrelevant.
            for (int level = 1; level <= 2; level++)
            {
                int f = 1 << level;
                int bx = dataTile.X << level, by = dataTile.Y << level;
                for (int dy = 0; dy < f; dy++)
                    for (int dx = 0; dx < f; dx++)
                        if (active.ContainsKey(new UnwrappedTileId(dataTile.Z + level, bx + dx, by + dy)))
                            return true;
            }
            return false;
        }

        #endregion

        #region Cell bookkeeping

        private void RefreshCells()
        {
            var mapRoot = _map.UnityContext.MapRoot;
            Vector3 playerLocal = Player != null
                ? mapRoot.InverseTransformPoint(Player.position)
                : Vector3.zero;
            Vector3 anchored = playerLocal - _root.localPosition;

            float cellSize = _span / CellsPerTileAxis;
            if (cellSize <= 1e-5f) return;

            // Exclusion data can arrive AFTER a cell was populated. Re-check the chain version
            // each cell decided on; stale cells despawn here and re-queue in the scan below,
            // so a wrong early decision self-corrects instead of standing forever.
            _chainVersionCache.Clear();
            _removeBuffer.Clear();
            if (UseExclusions)
            {
                foreach (var kvp in _cells)
                    if (ChainVersion(kvp.Value.SourceTileId) != kvp.Value.ExclusionVersion)
                        _removeBuffer.Add(kvp.Key);
                for (int i = 0; i < _removeBuffer.Count; i++)
                    DespawnCell(_removeBuffer[i]);
            }

            // Player position in GLOBAL cell coordinates.
            float pcx = (float)_anchor.X * CellsPerTileAxis + anchored.x / cellSize;
            float pcy = (float)_anchor.Y * CellsPerTileAxis - anchored.z / cellSize;

            float invMpu = 1f / Mathf.Max(1e-4f, _metresPerLocalUnit);
            float rSpawn = RadiusMetres * invMpu / cellSize;
            float rDespawn = (RadiusMetres + DespawnHysteresisMetres) * invMpu / cellSize;

            // GameObjects-mode edge fade, applied on the refresh tick (per-frame SetActive on
            // thousands of objects would cost more than it's worth; instanced mode gets the
            // per-frame smooth version).
            if (RenderMode == FlowerRenderMode.GameObjects && EdgeFadeFraction > 0.001f)
            {
                float radiusLocal = RadiusMetres * invMpu;
                float fadeStart = radiusLocal * Mathf.Clamp01(EdgeFadeFraction);
                float fadeInv = 1f / Mathf.Max(1e-3f, radiusLocal - fadeStart);
                foreach (var kvp in _cells)
                {
                    var list = kvp.Value.Instances;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var inst = list[i];
                        if (inst.Go == null) continue;
                        var lp = inst.Go.transform.localPosition;
                        float dx = lp.x - anchored.x, dz = lp.z - anchored.z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        float keep = 1f;
                        if (d > fadeStart)
                        {
                            float t = Mathf.Clamp01((d - fadeStart) * fadeInv);
                            keep = 1f - t * t * (3f - 2f * t);
                        }
                        bool show = inst.Rank < keep;
                        if (inst.Go.activeSelf != show) inst.Go.SetActive(show);
                    }
                }
            }

            // Despawn out-of-range cells first so a fast-moving player frees pool entries
            // before asking for more.
            _removeBuffer.Clear();
            foreach (var kvp in _cells)
                if (CellDistance(kvp.Value.Gx, kvp.Value.Gy, pcx, pcy) > rDespawn)
                    _removeBuffer.Add(kvp.Key);
            for (int i = 0; i < _removeBuffer.Count; i++)
                DespawnCell(_removeBuffer[i]);

            int minX = Mathf.FloorToInt(pcx - rSpawn);
            int maxX = Mathf.CeilToInt(pcx + rSpawn);
            int minY = Mathf.FloorToInt(pcy - rSpawn);
            int maxY = Mathf.CeilToInt(pcy + rSpawn);

            for (int gy = minY; gy <= maxY; gy++)
            {
                for (int gx = minX; gx <= maxX; gx++)
                {
                    // Exact circle-vs-cell-AABB, so the field edge follows the radius.
                    if (CellDistance(gx, gy, pcx, pcy) > rSpawn) continue;

                    long key = CellKey(gx, gy);
                    if (_cells.ContainsKey(key) || _pendingSet.Contains(key)) continue;

                    _pending.Add(new PendingCell { Key = key, Gx = gx, Gy = gy });
                    _pendingSet.Add(key);
                }
            }

            // Re-sort the whole backlog against the CURRENT player position — both
            // newly-found cells and ones deferred from earlier ticks. Sorted FARTHEST-FIRST
            // and consumed from the END of the list, which is the same near-first policy but
            // with O(1) removal; consuming a front-sorted list via RemoveAt(0) shifts every
            // remaining element and costs megabytes of moves per frame during the initial
            // fill of a large field.
            _pending.Sort((a, b) =>
                CellDistance(b.Gx, b.Gy, pcx, pcy).CompareTo(CellDistance(a.Gx, a.Gy, pcx, pcy)));
        }

        /// <summary>Distance in cell units from (px,py) to the nearest point of cell (gx,gy).</summary>
        private static float CellDistance(int gx, int gy, float px, float py)
        {
            float nx = Mathf.Clamp(px, gx, gx + 1f);
            float ny = Mathf.Clamp(py, gy, gy + 1f);
            float dx = px - nx, dy = py - ny;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private void ProcessPending()
        {
            // Don't consume cells there is no room to fill: a populated cell is marked done,
            // so populating at the cap would leave permanent holes. With the backlog sorted
            // nearest-first, the cells this starves are the OUTER ring — the correct ones.
            if (MaxLiveFlowers > 0 && _liveCount + _spawns.Count >= MaxLiveFlowers)
            {
                if (!_capWarned && _pending.Count > 0)
                {
                    _capWarned = true;
                    Debug.LogWarning($"[FlowerField] MaxLiveFlowers ({MaxLiveFlowers}) reached with " +
                                     $"{_pending.Count} cells still waiting. The field will stop short of " +
                                     "the radius. Expected total is pi x Radius^2 x FlowersPerSquareMetre " +
                                     $"= {Mathf.RoundToInt(Mathf.PI * RadiusMetres * RadiusMetres * FlowersPerSquareMetre)}; " +
                                     "raise MaxLiveFlowers or lower the density/radius.", this);
                }
                return;
            }
            _capWarned = false;

            int cells = 0, tests = 0;
            int index = _pending.Count - 1;               // nearest cell is at the END
            while (index >= 0 && cells < CellsPerFrame && tests < ExclusionTestsPerFrame)
            {
                // Re-check the cap PER CELL, not just at entry. One populated cell can queue
                // hundreds of jobs at high density, so checking only once per frame lets a
                // batch of cells overshoot the cap together; ProcessSpawns then drops the
                // overflow jobs while their cells stay registered as done — cells at the cap
                // boundary end up permanently half-filled. Stopping here instead leaves the
                // unprocessed cells pending, where they belong.
                if (MaxLiveFlowers > 0 && _liveCount + _spawns.Count >= MaxLiveFlowers) break;

                var pc = _pending[index];
                cells++;

                if (_cells.ContainsKey(pc.Key))
                {
                    _pendingSet.Remove(pc.Key);
                    _pending.RemoveAt(index);
                    index--;
                    continue;
                }

                if (PopulateCell(pc, ref tests))
                {
                    // Deferred (data still settling / grace running). Leave it in place and
                    // step toward the front, so one stuck tile can't block the cells behind
                    // it — bounded by CellsPerFrame so it can't spin either.
                    index--;
                }
                else
                {
                    _pendingSet.Remove(pc.Key);
                    _pending.RemoveAt(index);             // end-adjacent: O(1) amortized
                    index--;
                }
            }
        }

        /// <summary>Returns true if the cell should be retried later.</summary>
        private bool PopulateCell(PendingCell pc, ref int tests)
        {
            int tileX = FloorDiv(pc.Gx, CellsPerTileAxis);
            int tileY = FloorDiv(pc.Gy, CellsPerTileAxis);
            int cx = pc.Gx - tileX * CellsPerTileAxis;
            int cy = pc.Gy - tileY * CellsPerTileAxis;

            int n = 1 << DataZoom;
            if (tileY < 0 || tileY >= n) return false;      // past the poles
            var tileId = new CanonicalTileId(DataZoom, Mod(tileX, n), tileY);

            bool anyData = false;
            int chainVersion = 0;

            if (UseExclusions)
            {
                chainVersion = GatherChain(tileId, out anyData);

                // Settle on the CHAIN version, so data arriving at any level resets the clock.
                if (!_tileState.TryGetValue(tileId, out var st))
                {
                    st = new TileState { Version = chainVersion, ChangedAt = Time.time, FirstSeen = Time.time };
                    _tileState[tileId] = st;
                }
                else if (st.Version != chainVersion)
                {
                    st.Version = chainVersion;
                    st.ChangedAt = Time.time;
                }

                if (RequireExclusionData)
                {
                    // Still arriving — a tile mid-capture can have its roads but not its water.
                    if (Time.time - st.ChangedAt < ExclusionSettleSeconds) return true;

                    // Nothing anywhere in the chain: capture pending, or genuinely empty.
                    if (!anyData && Time.time - st.FirstSeen < ExclusionGraceSeconds) return true;
                }

                // Grace expired with nothing at any level. Two very different situations
                // produce that, and they need opposite handling:
                //
                //  * The tile IS generated in the SDK, yet capture wrote nothing → the tile
                //    genuinely has no water/roads/buildings (a wholly-park or rural tile).
                //    Mesh modifiers only run per FEATURE, so a featureless tile can never
                //    announce itself in the registry — absence after generation IS the
                //    announcement. Spawning here is correct; fail-closed starving these tiles
                //    is what produced tile-shaped bare gaps immune to density settings.
                //
                //  * The tile is NOT generated (still loading, or outside the view) → absence
                //    means nothing. Fail closed; version invalidation fills the cell when
                //    capture data arrives.
                if (!anyData && FailClosed && !IsTileGenerated(tileId))
                {
                    _cells[pc.Key] = new Cell
                    {
                        Key = pc.Key,
                        Gx = pc.Gx,
                        Gy = pc.Gy,
                        SourceTileId = tileId,
                        ExclusionVersion = chainVersion,
                        Generation = ++_cellGenerationCounter
                    };
                    return false;
                }
            }

            var cell = new Cell
            {
                Key = pc.Key,
                Gx = pc.Gx,
                Gy = pc.Gy,
                SourceTileId = tileId,
                ExclusionVersion = chainVersion,
                Generation = ++_cellGenerationCounter
            };
            _cells[pc.Key] = cell;

            float cellSize = _span / CellsPerTileAxis;
            float cellMetres = cellSize * _metresPerLocalUnit;
            float expected = cellMetres * cellMetres * FlowersPerSquareMetre;

            var rng = new System.Random(CellSeed(RandomSeed, DataZoom, tileId.X, tileId.Y, cx, cy));

            // --- Natural-distribution setup -------------------------------------------------
            // Uniform independent points are statistically random but PERCEPTUALLY a pattern:
            // even, texture-less salt-and-pepper. Nature is correlated. Three optional layers
            // add that correlation, all driven by DETERMINISTIC noise over ground position so
            // the walk-away-and-return identity guarantee is preserved:
            //   1. Density patches  — smooth noise thins acceptance: drifts and bare meadow.
            //   2. Species patches  — per-prefab noise biases the pick: same-species drifts.
            //   3. Clumps           — flowers spawn in small same-species clusters.
            float spanMetres = _span * _metresPerLocalUnit;
            bool densityPatches = DensityPatchScaleMetres > 0.01f && DensityPatchContrast > 0.001f;
            bool speciesPatches = SpeciesPatchScaleMetres > 0.01f && SpeciesPatchSharpness > 0.001f
                                  && Prefabs.Length > 1;
            int clumpMean = Mathf.Max(1, ClumpSize);
            bool clumping = clumpMean > 1 && ClumpRadiusMetres > 0.001f;
            float clumpRadius01 = clumping ? ClumpRadiusMetres / Mathf.Max(1f, spanMetres) : 0f;
            // Anchor-relative metres for noise input, keeping Perlin coordinates small (raw
            // world-tile metres are ~3e7, past float precision for smooth noise).
            float baseMx = (tileX - _anchor.X) * spanMetres;
            float baseMy = (tileY - _anchor.Y) * spanMetres;

            // Two octaves for the density field: the base octave sets where drifts live, a
            // finer octave roughens their boundaries so they stop reading as round Perlin
            // blobs. Weights fixed — organic-looking across the useful scale range.
            float cutoff = densityPatches ? Mathf.Clamp(DensityPatchContrast, 0f, 0.95f) * 0.7f : 0f;
            if (densityPatches)
            {
                // Thinning discards ~(1+cutoff)/2 of candidates on average; boost the target
                // so the OVERALL count stays close to density x area while the flowers
                // concentrate into the drifts. Capped so extreme contrast can't explode cost.
                expected *= Mathf.Min(4f, 2f / Mathf.Max(0.05f, 1f - cutoff));
            }

            // Fractional expectation resolved stochastically, so sub-1-per-cell densities
            // still hit the right average instead of rounding to zero everywhere.
            int count = Mathf.FloorToInt(expected);
            if (rng.NextDouble() < expected - count) count++;
            if (count <= 0) return false;

            // Attempts are budgeted per GROUP (a clump, or a single flower when clumping is
            // off) so clump size doesn't multiply the retry cost.
            int groupTarget = Mathf.Max(1, Mathf.CeilToInt(count / (float)clumpMean));
            int groupAttempts = groupTarget * Mathf.Max(1, AttemptsPerFlower);
            int made = 0;
            float inv = 1f / CellsPerTileAxis;

            for (int g = 0; g < groupAttempts && made < count; g++)
            {
                // Group centre, uniform in the cell.
                float gu = (float)rng.NextDouble();
                float gv = (float)rng.NextDouble();
                float gfx = (cx + gu) * inv;
                float gfy = (cy + gv) * inv;
                float gmx = baseMx + gfx * spanMetres;
                float gmy = baseMy + gfy * spanMetres;

                // Layer 1: density patches — accept/reject the WHOLE group by the smooth
                // field, so drift edges are soft (acceptance ramps) rather than hard cuts.
                if (densityPatches)
                {
                    float noise = 0.68f * Noise01(gmx / DensityPatchScaleMetres, gmy / DensityPatchScaleMetres, 0)
                                + 0.32f * Noise01(gmx / (DensityPatchScaleMetres * 0.27f), gmy / (DensityPatchScaleMetres * 0.27f), 7);
                    float accept = Mathf.Clamp01((noise - cutoff) / Mathf.Max(1e-3f, 1f - cutoff));
                    if ((float)rng.NextDouble() >= accept) continue;
                }

                // Layer 2: species patches — the clump's PATCH GROUP is chosen by pooled
                // weight x that group's own noise field raised to the sharpness power; the
                // concrete prefab is then a plain weighted pick among the group's members.
                // Where a group's field dominates, its colour dominates; at field borders
                // colours mix; variants within a colour mix everywhere.
                int pi;
                if (speciesPatches)
                {
                    int nGroups = _speciesScratch.Length;
                    float running = 0f;
                    for (int g2 = 0; g2 < nGroups; g2++)
                    {
                        float noise = Noise01(gmx / SpeciesPatchScaleMetres, gmy / SpeciesPatchScaleMetres, g2 + 1);
                        running += _groupWeights[g2]
                                   * Mathf.Pow(Mathf.Lerp(0.06f, 1f, noise), SpeciesPatchSharpness);
                        _speciesScratch[g2] = running;
                    }
                    if (running <= 0f) continue;
                    float pick = (float)rng.NextDouble() * running;
                    int gi = 0;
                    while (gi < nGroups - 1 && _speciesScratch[gi] <= pick) gi++;

                    var cum = _groupMemberCumulative[gi];
                    float total = cum[cum.Length - 1];
                    if (total <= 0f) continue;
                    float mp = (float)rng.NextDouble() * total;
                    int mi = 0;
                    while (mi < cum.Length - 1 && cum[mi] <= mp) mi++;
                    pi = _groupMembers[gi][mi];
                }
                else
                {
                    pi = ExclusionSampling.PickIndex(_prefabCumulative, Prefabs.Length, rng);
                }
                if (pi < 0 || Prefabs[pi] == null) continue;

                // Layer 3: clumps — members scatter around the centre, denser toward it
                // (linear-radius disc). Members may drift slightly past the cell edge; that's
                // fine and desirable — it hides the grid.
                //
                // Three touches beyond the basic cluster, tuned by eye rather than exposed
                // as knobs:
                //  * Radius jitter (0.6-1.4x) and occasional SUPER-CLUMPS (~10%: double the
                //    members, 1.6x the spread) — a field of identically-sized clusters reads
                //    as manufactured just like a field of identically-spaced flowers did.
                //  * A shared per-clump size tendency: real clusters are cohorts, so members
                //    lean small or large together, with individual jitter on top.
                //  * Members shrink slightly toward the clump rim — younger growth at the
                //    edges — which softens each cluster's silhouette.
                int members = clumping ? 1 + rng.Next(2 * clumpMean - 1) : 1; // mean = clumpMean
                float clumpSpread = clumpRadius01 * (0.6f + 1.2f * (float)rng.NextDouble());
                if (clumping && rng.NextDouble() < 0.10)
                {
                    members *= 2;
                    clumpSpread *= 1.6f;
                }
                float clumpScaleBias = 0.85f + 0.3f * (float)rng.NextDouble();

                for (int m = 0; m < members && made < count; m++)
                {
                    float fx = gfx, fy = gfy;
                    float rimT = 0f;
                    if (clumping && m > 0)
                    {
                        float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                        rimT = (float)rng.NextDouble();
                        float rad = rimT * clumpSpread;
                        fx += Mathf.Cos(ang) * rad;
                        fy += Mathf.Sin(ang) * rad;

                        // A member pushed past the TILE boundary would be exclusion-tested
                        // against THIS tile's geometry, which does not reliably cover the
                        // neighbour — an untestable point, and untestable must mean unspawned
                        // (a member just across the line can sit inside the neighbour's
                        // building or lake edge with nothing to reject it). Skipping costs a
                        // sub-1% density dip in a ~2 m band along tile borders; invisible.
                    }
                    if (fx < 0f || fx > 1f || fy < 0f || fy > 1f) continue;

                    // Species sprinkle: mostly the clump's species, but occasionally an
                    // individual of another kind mixed in — the reference look is a dominant
                    // colour with strays through it, not perfectly segregated drifts.
                    int memberPrefab = pi;
                    if (speciesPatches && rng.NextDouble() < 0.15)
                    {
                        int alt = ExclusionSampling.PickIndex(_prefabCumulative, Prefabs.Length, rng);
                        if (alt >= 0 && Prefabs[alt] != null) memberPrefab = alt;
                    }

                    tests++;
                    if (UseExclusions && anyData && IsExcludedByChain(fx, fy)) continue;

                    float ax = ((tileX - _anchor.X) + fx) * _span;
                    float az = -(((tileY - _anchor.Y) + fy) * _span);

                    float y = YOffset;
                    if (SnapToTerrain)
                    {
                        TileXYToLatLon(tileX + fx, tileY + fy, DataZoom, out var la, out var lo);
                        if (_map.TryGetElevation(new LatitudeLongitude(la, lo), out var elevation))
                            y += elevation;
                    }

                    float sMul = Mathf.Max(1e-4f, ScaleMultiplier)
                                 * (clumping ? clumpScaleBias : 1f)
                                 * Mathf.Lerp(1f, 0.8f, rimT)
                                 * (1f + ((float)rng.NextDouble() * 2f - 1f) * ScaleJitter);
                    if (PrefabSizeIsMetres) sMul /= Mathf.Max(1e-4f, _metresPerLocalUnit);

                    // COMPOSE the random yaw with the prefab's authored rotation — and order
                    // matters. yaw * base (used here) spins the AUTHORED POSE about world up:
                    // a tilted prefab stays tilted, and its lean direction randomizes with the
                    // yaw. The previous order, base * yaw, yawed the mesh in its own pre-
                    // rotation frame FIRST and applied the authored rotation after — so every
                    // instance leaned the same world direction with the geometry merely spun
                    // underneath, and for prefabs whose rotation lives on the root the spawned
                    // roots read as plain (0, yaw, 0), as if the authored rotation were lost.
                    var rot = RandomYRotation
                        ? Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f) * _prefabBaseRot[memberPrefab]
                        : _prefabBaseRot[memberPrefab];

                    _spawns.Enqueue(new SpawnJob
                    {
                        CellKey = pc.Key,
                        PrefabIndex = memberPrefab,
                        LocalPos = new Vector3(ax, y, az),
                        Rot = rot,
                        Scale = sMul,
                        Rank = (float)rng.NextDouble(),
                        Generation = cell.Generation
                    });
                    made++;
                }
            }

            return false;
        }

        private void ProcessSpawns()
        {
            int done = 0;
            while (_spawns.Count > 0 && done < SpawnsPerFrame)
            {
                var job = _spawns.Dequeue();
                done++;

                // The cell may have gone out of range while this job sat in the queue — or
                // been invalidated and REPOPULATED under the same key, in which case this job
                // belongs to the dead predecessor and its position was vetted against stale
                // exclusion data. Drop it; the new population regenerates the cell in full.
                if (!_cells.TryGetValue(job.CellKey, out var cell)) continue;
                if (cell.Generation != job.Generation) continue;
                if (MaxLiveFlowers > 0 && _liveCount >= MaxLiveFlowers) continue;

                var scale = _prefabBaseScale[job.PrefabIndex] * job.Scale;

                if (RenderMode == FlowerRenderMode.GpuInstanced)
                {
                    if (_prefabParts[job.PrefabIndex].Count == 0) continue;
                    cell.Instances.Add(new Instance
                    {
                        Go = null,
                        PrefabIndex = job.PrefabIndex,
                        Local = Matrix4x4.TRS(job.LocalPos, job.Rot, scale),
                        Rank = job.Rank
                    });
                    _batchDirty = true;
                    _liveCount++;
                    continue;
                }

                var go = Rent(job.PrefabIndex);
                if (go == null) continue;

                var t = go.transform;
                t.SetParent(_root, false);
                t.localPosition = job.LocalPos;
                t.localRotation = job.Rot;
                t.localScale = scale;
                go.SetActive(true);

                cell.Instances.Add(new Instance { Go = go, PrefabIndex = job.PrefabIndex, Rank = job.Rank });
                _liveCount++;
            }
        }

        private void DespawnCell(long key)
        {
            if (!_cells.TryGetValue(key, out var cell)) return;
            for (int i = 0; i < cell.Instances.Count; i++)
                Return(cell.Instances[i]);
            cell.Instances.Clear();
            _cells.Remove(key);
            _batchDirty = true;
        }

        private void DespawnAll()
        {
            foreach (var cell in _cells.Values)
            {
                for (int i = 0; i < cell.Instances.Count; i++)
                    Return(cell.Instances[i]);
                cell.Instances.Clear();
            }
            _cells.Clear();
            _pending.Clear();
            _pendingSet.Clear();
            _spawns.Clear();
        }

        /// <summary>
        /// Keeps the registry bounded. Distance-based (never per-entity): the vector module
        /// serves revisited tiles from its own cache without re-running mesh modifiers, so a
        /// deleted entry near the play area would be unrecoverable. Threshold is expressed in
        /// z16 tiles and scaled per entry zoom so one setting means one ground distance.
        /// </summary>
        private void PruneRegistry()
        {
            if (!UseExclusions || PruneRadiusTiles <= 0) return;

            var centre = _map.MapInformation.LatitudeLongitude;
            ExclusionRegistry.GetTileIds(_tileIdBuffer);

            for (int i = 0; i < _tileIdBuffer.Count; i++)
            {
                var id = _tileIdBuffer[i];
                LatLonToTileXY(centre.Latitude, centre.Longitude, id.Z, out var ptx, out var pty);
                float thresholdAtZ = PruneRadiusTiles / (float)(1 << (16 - Mathf.Min(16, id.Z)));
                if (Math.Abs(ptx - id.X) > thresholdAtZ || Math.Abs(pty - id.Y) > thresholdAtZ)
                {
                    ExclusionRegistry.RemoveForPrune(id);
                    _tileState.Remove(id);
                }
            }

            // _tileState also holds entries for FEATURELESS tiles, which by definition never
            // appear in the registry's id list above — without this sweep they accumulate for
            // the whole session as the player travels. Same distance rule.
            _tileIdBuffer.Clear();
            foreach (var kvp in _tileState) _tileIdBuffer.Add(kvp.Key);
            for (int i = 0; i < _tileIdBuffer.Count; i++)
            {
                var id = _tileIdBuffer[i];
                LatLonToTileXY(centre.Latitude, centre.Longitude, id.Z, out var ptx, out var pty);
                float thresholdAtZ = PruneRadiusTiles / (float)(1 << (16 - Mathf.Min(16, id.Z)));
                if (Math.Abs(ptx - id.X) > thresholdAtZ || Math.Abs(pty - id.Y) > thresholdAtZ)
                    _tileState.Remove(id);
            }
        }

        #endregion

        #region Instanced rendering

        /// <summary>
        /// Submits the whole field as a handful of instanced draw calls. Flowers are the same
        /// few meshes repeated thousands of times — precisely what instancing is for; unlike
        /// the buildings (all different meshes), no merge step is needed and no per-instance
        /// addressability is lost.
        /// </summary>
        private void RenderInstanced()
        {
            if (_batches == null || _root == null) return;
            if (_batchDirty) RebuildBatches();

            // Matrices are in ANCHORED space; the map's own transform is folded in here, so
            // map movement costs one matrix multiply per flower rather than a Transform write.
            var rootMatrix = _root.localToWorldMatrix;

            Vector3 centre = Player != null ? Player.position : _root.position;
            float radiusWorld = (RadiusMetres + DespawnHysteresisMetres)
                                / Mathf.Max(1e-4f, _metresPerLocalUnit)
                                * Mathf.Abs(_root.lossyScale.x);
            var bounds = new Bounds(centre, Vector3.one * (radiusWorld * 2f + 10f));
            var shadowMode = CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;

            // Edge fade: per-flower visibility against a smooth falloff of distance to the
            // player, evaluated fresh every frame in ANCHORED space, so the gradient tracks
            // the walking player with no respawning and no cell churn.
            bool fade = EdgeFadeFraction > 0.001f;
            float px = 0f, pz = 0f, fadeStart = 0f, fadeEnd = 1f, fadeInv = 1f;
            if (fade)
            {
                var mapRoot = _map.UnityContext.MapRoot;
                Vector3 pAnchored = (Player != null
                    ? mapRoot.InverseTransformPoint(Player.position)
                    : Vector3.zero) - _root.localPosition;
                px = pAnchored.x; pz = pAnchored.z;
                float radiusLocal = RadiusMetres / Mathf.Max(1e-4f, _metresPerLocalUnit);
                fadeStart = radiusLocal * Mathf.Clamp01(EdgeFadeFraction);
                fadeEnd = Mathf.Max(fadeStart + 1e-3f, radiusLocal);
                fadeInv = 1f / (fadeEnd - fadeStart);
            }

            for (int b = 0; b < _batches.Count; b++)
            {
                var batch = _batches[b];
                int count = batch.Matrices.Count;
                if (count == 0 || batch.Mesh == null || batch.Material == null) continue;

                if (_scratch.Length < count) _scratch = new Matrix4x4[Mathf.NextPowerOfTwo(count)];

                int visible = 0;
                for (int i = 0; i < count; i++)
                {
                    var m = batch.Matrices[i];
                    if (fade)
                    {
                        // Anchored position lives in column 3 of the local TRS.
                        float dx = m.m03 - px, dz = m.m23 - pz;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d > fadeStart)
                        {
                            float t = Mathf.Clamp01((d - fadeStart) * fadeInv);
                            float keep = 1f - t * t * (3f - 2f * t);   // smoothstep down
                            if (batch.Ranks[i] >= keep) continue;
                        }
                    }
                    _scratch[visible++] = rootMatrix * m;
                }
                if (visible == 0) continue;

                var rp = new RenderParams(batch.Material)
                {
                    worldBounds = bounds,
                    shadowCastingMode = shadowMode,
                    receiveShadows = ReceiveShadows,
                    layer = gameObject.layer
                };

                const int chunk = 1023; // classic safe instance-buffer limit
                for (int off = 0; off < visible; off += chunk)
                    Graphics.RenderMeshInstanced(rp, batch.Mesh, batch.SubMesh, _scratch,
                                                 Mathf.Min(chunk, visible - off), off);
            }
        }

        /// <summary>
        /// Flattens per-cell instances into contiguous matrix arrays per batch, folding in
        /// each part's offset from the prefab root. Runs only when a cell changed.
        /// </summary>
        private void RebuildBatches()
        {
            for (int b = 0; b < _batches.Count; b++)
            {
                _batches[b].Matrices.Clear();
                _batches[b].Ranks.Clear();
            }

            foreach (var cell in _cells.Values)
            {
                var list = cell.Instances;
                for (int i = 0; i < list.Count; i++)
                {
                    var inst = list[i];
                    if (inst.Go != null) continue;               // GameObject-mode entry
                    if (inst.PrefabIndex < 0 || inst.PrefabIndex >= _prefabParts.Length) continue;

                    var parts = _prefabParts[inst.PrefabIndex];
                    for (int p = 0; p < parts.Count; p++)
                    {
                        var batch = _batches[parts[p].BatchIndex];
                        batch.Matrices.Add(inst.Local * parts[p].Offset);
                        batch.Ranks.Add(inst.Rank);
                    }
                }
            }
            _batchDirty = false;
        }

        /// <summary>
        /// Records every enabled MeshRenderer in the prefab as an instanceable part with its
        /// root-relative transform. If the prefab has an LODGroup, only LOD0 is taken —
        /// otherwise every LOD level would draw simultaneously.
        /// </summary>
        private void CollectParts(GameObject prefab, List<PrefabPart> into)
        {
            var lodGroup = prefab.GetComponentInChildren<LODGroup>(true);
            Renderer[] renderers;
            if (lodGroup != null)
            {
                var lods = lodGroup.GetLODs();
                renderers = lods.Length > 0 ? lods[0].renderers : new Renderer[0];
            }
            else
            {
                renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            }

            for (int r = 0; r < renderers.Length; r++)
            {
                var mr = renderers[r] as MeshRenderer;
                if (mr == null || !mr.enabled || !mr.gameObject.activeSelf) continue;

                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var offset = RelativeMatrix(mr.transform, prefab.transform);
                var mats = mr.sharedMaterials;
                int subMeshes = mf.sharedMesh.subMeshCount;

                for (int sm = 0; sm < subMeshes; sm++)
                {
                    var mat = sm < mats.Length ? mats[sm] : null;
                    if (mat == null) continue;
                    into.Add(new PrefabPart { BatchIndex = GetOrAddBatch(mf.sharedMesh, mat, sm), Offset = offset });
                }
            }
        }

        private int GetOrAddBatch(Mesh mesh, Material material, int subMesh)
        {
            for (int i = 0; i < _batches.Count; i++)
                if (_batches[i].Mesh == mesh && _batches[i].Material == material && _batches[i].SubMesh == subMesh)
                    return i;

            _batches.Add(new InstancedBatch { Mesh = mesh, Material = material, SubMesh = subMesh });
            return _batches.Count - 1;
        }

        /// <summary>
        /// Accumulates local TRS up the hierarchy rather than reading world matrices, because
        /// a prefab ASSET is not in a scene and its world transform is not meaningful. Stops
        /// at (and excludes) the root, whose TRS the spawn path applies.
        /// </summary>
        private static Matrix4x4 RelativeMatrix(Transform child, Transform root)
        {
            var m = Matrix4x4.identity;
            var t = child;
            int guard = 0;
            while (t != null && t != root && guard++ < 64)
            {
                m = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * m;
                t = t.parent;
            }
            return m;
        }

        #endregion

        #region Pooling (GameObjects mode)

        private GameObject Rent(int prefabIndex)
        {
            if (_pool.TryGetValue(prefabIndex, out var stack) && stack.Count > 0)
            {
                var pooled = stack.Pop();
                if (pooled != null) return pooled;
            }
            var prefab = Prefabs[prefabIndex];
            if (prefab == null) return null;
            var go = Instantiate(prefab);
            go.SetActive(false);
            return go;
        }

        private void Return(Instance inst)
        {
            _liveCount--;
            if (inst.Go == null) return;
            inst.Go.SetActive(false);
            inst.Go.transform.SetParent(_root, false);
            if (!_pool.TryGetValue(inst.PrefabIndex, out var stack))
            {
                stack = new Stack<GameObject>();
                _pool[inst.PrefabIndex] = stack;
            }
            stack.Push(inst.Go);
        }

        #endregion

        #region Maths helpers

        private static void LatLonToTileXY(double lat, double lon, int zoom, out double tx, out double ty)
        {
            double n = 1 << zoom;
            double latRad = lat * Math.PI / 180.0;
            tx = (lon + 180.0) / 360.0 * n;
            ty = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
        }

        private static void TileXYToLatLon(double tx, double ty, int zoom, out double lat, out double lon)
        {
            double n = 1 << zoom;
            lon = tx / n * 360.0 - 180.0;
            double m = Math.PI * (1.0 - 2.0 * ty / n);
            lat = Math.Atan(Math.Sinh(m)) * 180.0 / Math.PI;
        }

        private static long CellKey(int gx, int gy)
        {
            // 24 bits each: z16 x 256 subdivisions is 2^24 cells across, the exact ceiling.
            return ((long)(gx & 0xFFFFFF) << 24) | (long)(gy & 0xFFFFFF);
        }

        private static int CellSeed(int seed, int z, int x, int y, int cx, int cy)
        {
            unchecked
            {
                int h = seed;
                h = h * 486187739 + z;
                h = h * 486187739 + x;
                h = h * 486187739 + y;
                h = h * 486187739 + cx;
                h = h * 486187739 + cy;
                return h;
            }
        }

        /// <summary>
        /// Deterministic smooth noise in [0,1]. Channel selects an independent field (0 =
        /// density, 1+ = per-species) via a large offset; RandomSeed shifts all fields so
        /// changing the seed reshuffles the patch layout along with the flowers. Inputs are
        /// anchor-relative metres over patch scale, which keeps coordinates small enough for
        /// Perlin to stay smooth (world-absolute metres exceed float precision). Consequence:
        /// the patch layout is anchored per session start location, and shifts if the field
        /// re-anchors after ~18 km of travel — the cells then regenerate consistently against
        /// the new layout, so nothing tears; the drifts just land differently.
        /// </summary>
        private float Noise01(float x, float y, int channel)
        {
            float ox = (RandomSeed & 0xFFFF) * 0.137f + channel * 391.71f;
            float oy = ((RandomSeed >> 8) & 0xFFFF) * 0.291f + channel * 173.13f;
            return Mathf.Clamp01(Mathf.PerlinNoise(x + ox, y + oy));
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -(((-a) + b - 1) / b);

        private static int Mod(int a, int m)
        {
            int r = a % m;
            return r < 0 ? r + m : r;
        }

        #endregion

        #region Diagnostics

        /// <summary>
        /// Dumps, for every DataZoom tile the field currently touches, what the registry holds
        /// at each chain level (z16..z13), including LIFETIME writes — which distinguishes
        /// "never captured" from "captured and then deleted". Run while looking at a broken
        /// area; this one log splits the search space decisively.
        /// </summary>
        [ContextMenu("Dump Exclusion Tiles")]
        public void DumpExclusionTiles()
        {
            if (_map == null || !_anchorSet) { Debug.Log("[FlowerField] map not ready"); return; }

            var seen = new HashSet<CanonicalTileId>();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[FlowerField] exclusion dump, {_cells.Count} cells, levels z{DataZoom}..z{DataZoom - ParentSearchLevels}:");

            var dumpSnap = new ExclusionSampling.Snapshot();
            foreach (var cell in _cells.Values)
            {
                if (!seen.Add(cell.SourceTileId)) continue;
                var id = cell.SourceTileId;
                sb.Append($"  tile {id.Z}/{id.X}/{id.Y}: ");

                int z = id.Z, tx = id.X, ty = id.Y;
                bool any = false;
                for (int step = 0; step <= ParentSearchLevels && z >= 1; step++)
                {
                    var lvl = new CanonicalTileId(z, tx, ty);
                    int lifetime = ExclusionRegistry.GetLifetimeWrites(lvl);
                    if (ExclusionSampling.TryTake(lvl, dumpSnap, out int ver))
                    {
                        sb.Append($"[z{z} v{ver} meshes={dumpSnap.Meshes.Count} lines={dumpSnap.Lines.Count} life={lifetime}] ");
                        any = true;
                    }
                    else if (lifetime > 0)
                    {
                        sb.Append($"[z{z} CLEARED! life={lifetime} current=0] ");
                        any = true;
                    }
                    tx >>= 1; ty >>= 1; z--;
                }
                if (!any)
                    sb.Append(IsTileGenerated(id)
                        ? "NO DATA, TILE GENERATED (genuinely featureless -> spawns open)"
                        : "NO DATA, TILE NOT GENERATED (fail-closed)");
                sb.AppendLine($" | cellVer={cell.ExclusionVersion} flowers={cell.Instances.Count}");
            }
            Debug.Log(sb.ToString());
        }

        #endregion

        #region Gizmos

        private void OnDrawGizmosSelected()
        {
            if (!DebugGizmos || _root == null || _map == null) return;

            var mapRoot = _map.UnityContext.MapRoot;
            if (mapRoot == null) return;

            Vector3 centre = Player != null ? Player.position : mapRoot.position;
            float invMpu = 1f / Mathf.Max(1e-4f, _metresPerLocalUnit);
            float radiusWorld = RadiusMetres * invMpu * mapRoot.lossyScale.x;

            Gizmos.color = Color.magenta;
            DrawCircle(centre, radiusWorld);
            Gizmos.color = new Color(1f, 0f, 1f, 0.35f);
            DrawCircle(centre, radiusWorld + DespawnHysteresisMetres * invMpu * mapRoot.lossyScale.x);
        }

        private static void DrawCircle(Vector3 centre, float radius)
        {
            const int steps = 48;
            Vector3 prev = centre + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= steps; i++)
            {
                float a = i / (float)steps * Mathf.PI * 2f;
                Vector3 next = centre + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }

        #endregion
    }
}