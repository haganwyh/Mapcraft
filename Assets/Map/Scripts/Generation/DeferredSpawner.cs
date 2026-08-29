using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.GameObjectModifiers
{
    /// <summary>
    /// Spreads BOTH the exclusion-testing and the GameObject instantiation across frames,
    /// so a tile-load that needs to scatter many parks doesn't hitch.
    ///
    /// Pipeline, per frame:
    ///   1) Sampling phase  (budget = TestsPerFrame): pull candidate points from active
    ///      ScatterTasks, run the exclusion test, and turn survivors into spawn jobs.
    ///   2) Spawn phase     (budget = SpawnsPerFrame): instantiate from the spawn-job queue.
    ///
    /// Both phases are capped independently. Work total is unchanged; it's just spread out,
    /// trading a spike for a smooth trickle (trees pop in over a moment).
    ///
    /// Auto-creates a hidden singleton; no scene setup needed.
    ///
    /// The geometry tests live in ExclusionSampling, shared with FlowerFieldManager.
    /// </summary>
    public class DeferredSpawner : MonoBehaviour
    {
        // ---- Shared per-park handle (Run fills it; Finalize cancels it) ----
        public class ScatterBatch
        {
            public readonly List<GameObject> Spawned = new();
            public bool Cancelled;
            public Action<GameObject> OnSpawned; // optional, invoked per instance
        }

        // ---- A deferred "scatter this park" job: sampling happens later, on the budget ----
        public class ScatterTask
        {
            // Mesh sampling scaffold (local space), captured from Run.
            public Vector3[] Verts;
            public int[] Tris;
            public float[] Cumulative;   // per-triangle cumulative area
            public float TotalArea;

            public Transform Parent;     // ve.Transform
            public GameObject[] Prefabs;
            public float[] PrefabCumulative; // cumulative weights, parallel to Prefabs; null = uniform
            public float ScaleMultiplier;
            public float ScaleJitter;
            public bool RandomYRotation;

            // Exclusion data (may be null) + test params.
            //
            // This is a SNAPSHOT, not the registry's live TileExclusions. A task outlives the
            // frame it was created in, and a tile that unloads and reloads appends into the
            // same live object — which would mutate the lists mid-iteration on a background
            // thread. The snapshot copies the outer list once, under the registry lock.
            public ExclusionSampling.Snapshot Exclusions;
            public float MarginSq;
            public float RoadBufferSq;

            public System.Random Rng;
            public int Target;           // how many we want to place
            public int Placed;           // how many enqueued for spawn so far
            public int Attempts;         // sampling attempts spent
            public int MaxAttempts;      // cap (retry budget)

            public ScatterBatch Batch;

            public bool Done => Batch.Cancelled || Placed >= Target || Attempts >= MaxAttempts;
        }

        private struct SpawnJob
        {
            public GameObject Prefab;
            public Transform Parent;
            public Vector3 LocalPosition;
            public Vector3 Scale;
            public Quaternion LocalRotation;
            public ScatterBatch Batch;
        }

        private static DeferredSpawner _instance;
        public static DeferredSpawner Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("~DeferredSpawner") { hideFlags = HideFlags.HideAndDontSave };
                    _instance = go.AddComponent<DeferredSpawner>();
                }
                return _instance;
            }
        }

        // Independent per-frame budgets.
        public int TestsPerFrame = 120;   // exclusion-tests (sampling) per frame
        public int SpawnsPerFrame = 40;   // instantiations per frame

        private readonly Queue<ScatterTask> _tasks = new();
        private readonly Queue<SpawnJob> _spawns = new();

        public void EnqueueTask(ScatterTask task) => _tasks.Enqueue(task);

        public void SetBudgets(int testsPerFrame, int spawnsPerFrame)
        {
            if (testsPerFrame > 0) TestsPerFrame = testsPerFrame;
            if (spawnsPerFrame > 0) SpawnsPerFrame = spawnsPerFrame;
        }

        private void Update()
        {
            RunSamplingPhase();
            RunSpawnPhase();
        }

        // Phase 1: sample candidate points and test exclusions, up to TestsPerFrame.
        private void RunSamplingPhase()
        {
            int budget = Mathf.Max(1, TestsPerFrame);
            int used = 0;

            while (used < budget && _tasks.Count > 0)
            {
                var task = _tasks.Peek();

                if (task.Batch.Cancelled || task.Parent == null)
                {
                    _tasks.Dequeue();
                    continue;
                }

                // Work on this task until it's done or we run out of budget this frame.
                while (used < budget && !task.Done)
                {
                    task.Attempts++;
                    used++;

                    // Pick an area-weighted triangle, then a uniform point inside it.
                    float r = (float)task.Rng.NextDouble() * task.TotalArea;
                    int ti = ExclusionSampling.LowerBound(task.Cumulative, r);
                    var a = task.Verts[task.Tris[ti * 3 + 0]];
                    var b = task.Verts[task.Tris[ti * 3 + 1]];
                    var c = task.Verts[task.Tris[ti * 3 + 2]];

                    float u = (float)task.Rng.NextDouble();
                    float v = (float)task.Rng.NextDouble();
                    if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                    var pos = a + u * (b - a) + v * (c - a);

                    if (float.IsNaN(pos.x) || float.IsInfinity(pos.x))
                        continue;

                    if (task.Exclusions != null &&
                        ExclusionSampling.IsExcluded(task.Exclusions, pos.x, pos.z,
                                                     task.MarginSq, task.RoadBufferSq))
                        continue;

                    int idx = ExclusionSampling.PickIndex(task.PrefabCumulative, task.Prefabs.Length, task.Rng);
                    if (idx < 0) continue;
                    var prefab = task.Prefabs[idx];
                    if (prefab == null)
                        continue;

                    float jitter = 1f + ((float)task.Rng.NextDouble() * 2f - 1f) * task.ScaleJitter;
                    float totalMultiplier = Mathf.Max(0.0001f, task.ScaleMultiplier) * jitter;
                    Vector3 scale = prefab.transform.localScale * totalMultiplier;

                    var baseRot = prefab.transform.localRotation;
                    var rot = task.RandomYRotation
                        ? baseRot * Quaternion.Euler(0f, (float)task.Rng.NextDouble() * 360f, 0f)
                        : baseRot;

                    _spawns.Enqueue(new SpawnJob
                    {
                        Prefab = prefab,
                        Parent = task.Parent,
                        LocalPosition = pos,
                        Scale = scale,
                        LocalRotation = rot,
                        Batch = task.Batch
                    });
                    task.Placed++;
                }

                if (task.Done)
                    _tasks.Dequeue();
            }
        }

        // Phase 2: instantiate from the spawn queue, up to SpawnsPerFrame.
        private void RunSpawnPhase()
        {
            int budget = Mathf.Max(1, SpawnsPerFrame);
            int done = 0;

            while (done < budget && _spawns.Count > 0)
            {
                var job = _spawns.Dequeue();
                if (job.Batch == null || job.Batch.Cancelled || job.Parent == null || job.Prefab == null)
                    continue;

                var go = UnityEngine.Object.Instantiate(job.Prefab, job.Parent);
                go.transform.localPosition = job.LocalPosition;
                go.transform.localScale = job.Scale;
                go.transform.localRotation = job.LocalRotation;

                job.Batch.Spawned.Add(go);
                job.Batch.OnSpawned?.Invoke(go);
                done++;
            }
        }
    }
}