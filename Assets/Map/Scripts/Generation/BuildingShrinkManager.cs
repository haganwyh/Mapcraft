using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.GameObjectModifiers
{
    /// <summary>
    /// Pushes the cone parameters to the GPU once per frame as global shader uniforms. The shrink
    /// happens entirely in the building shader's vertex stage (BuildingShrink.hlsl) using the
    /// per-building anchor baked into UV channel 1 by BuildingAnchorMeshModifier. So there is NO
    /// per-building CPU work, and it works with "Merge Objects" ON.
    ///
    /// MODEL (matches the shader): a cone from the player toward the camera in XZ. A building
    /// shrinks if its anchor is in front of the player toward the camera, within MaxDistance, and
    /// within the cone half-width (widening with distance, floored at MinHalfWidth, soft edge).
    /// </summary>
    [DefaultExecutionOrder(100)] // after camera/player movement, so globals use the final pose
    public class BuildingShrinkManager : MonoBehaviour
    {
        public static BuildingShrinkManager Instance { get; private set; }

        [Header("References")]
        [Tooltip("The player transform (the character the camera follows).")]
        public Transform Player;

        [Tooltip("The camera transform. If empty, Camera.main is used.")]
        public Transform CameraTransform;

        [Header("Occluder Region (cone from player toward camera)")]
        [Tooltip("When ON, the cone's reach is the horizontal distance from the player to the " +
                 "camera (times MaxDistanceScale), so the cone always reaches the camera no matter " +
                 "how far it's zoomed — consistent effect at any camera distance. When OFF, the " +
                 "fixed MaxDistance below is used.")]
        public bool MaxDistanceFromCamera = true;

        [Tooltip("Multiplier on the camera distance when MaxDistanceFromCamera is ON. 1 = reach " +
                 "exactly to the camera, 0.8 = stop a bit short, 1.2 = overshoot past it.")]
        public float MaxDistanceScale = 1f;

        [Tooltip("Fixed cone reach (world units), used only when MaxDistanceFromCamera is OFF.")]
        public float MaxDistance = 150f;

        [Tooltip("Half-angle of the cone (degrees) from the player toward the camera. 30-50 typical.")]
        [Range(5f, 89f)] public float ConeHalfAngle = 40f;

        [Tooltip("Radius (world units) of the sphere around the player that always shrinks (in " +
                 "ALL directions, even behind). The cone widens out of this ball toward the " +
                 "camera. This is the swept-sphere's ball radius — raise it to shrink a bigger " +
                 "area immediately around the player.")]
        public float MinHalfWidth = 25f;

        [Tooltip("Soft edge (world units) outside the cone's SIDE where the shrink fades to zero.")]
        public float EdgeFalloff = 20f;

        [Tooltip("Softness (world units) of the FORWARD entry and MaxDistance edges. SMALL = snappy " +
                 "(buildings shrink fast the moment they're in front of you and inside the cone). " +
                 "Larger = buildings ease in over distance as they enter. Set near 0 for an instant " +
                 "forward snap. This is separate from EdgeFalloff so the cone's sides can stay soft " +
                 "while the forward shrink is snappy.")]
        public float ForwardFade = 2f;

        [Header("Shrink")]
        [Tooltip("Fully-shrunk height as a fraction of original. 0.1 = 10% tall. 0 = flat.")]
        [Range(0f, 1f)] public float MinHeightFactor = 0.12f;

        [Header("Alpha Fade (requires a Transparent material)")]
        [Tooltip("Shrink amount (0..1) at which the building STARTS fading. Below this it stays " +
                 "fully opaque. 0.7 = stays solid until 70% shrunk, then fades. Keeps partly-" +
                 "shrunk buildings visible; only ghosts the nearly-flattened ones.")]
        [Range(0f, 1f)] public float FadeStart = 0.7f;

        [Tooltip("Alpha at full shrink (shrink = 1). 0 = fully invisible, 0.3 = faint ghost. " +
                 "Only takes effect if the building material's Surface Type is Transparent.")]
        [Range(0f, 1f)] public float MinAlpha = 0.15f;

        [Header("Per-Building Color")]
        [Tooltip("Strength of the random per-building color variation around the base color " +
                 "(0 = all identical, ~0.05-0.2 = subtle variety, larger = bolder). Centered on " +
                 "zero so the overall palette stays the base color.")]
        [Range(0f, 1f)] public float TintStrength = 0.12f;

        [Tooltip("Warm/positive end of the tint axis. Buildings that randomly lean positive tint " +
                 "toward this (e.g. light yellow).")]
        [ColorUsage(false, false)] public Color TintColorA = new Color(1f, 0.95f, 0.7f); // light warm

        [Tooltip("Cold/negative end of the tint axis. Buildings that randomly lean negative tint " +
                 "toward this (e.g. light blue). A building is EITHER warm OR cold, never mixed.")]
        [ColorUsage(false, false)] public Color TintColorB = new Color(0.7f, 0.85f, 1f); // light cool

        [Header("Debug")]
        public bool DebugGizmos = false;

        private static readonly int IdPlayerXZ = Shader.PropertyToID("_PlayerXZ");
        private static readonly int IdConeDirXZ = Shader.PropertyToID("_ConeDirXZ");
        private static readonly int IdConeSideXZ = Shader.PropertyToID("_ConeSideXZ");
        private static readonly int IdMaxDistance = Shader.PropertyToID("_MaxDistance");
        private static readonly int IdTanHalfAngle = Shader.PropertyToID("_TanHalfAngle");
        private static readonly int IdMinHalfWidth = Shader.PropertyToID("_MinHalfWidth");
        private static readonly int IdEdgeFalloff = Shader.PropertyToID("_EdgeFalloff");
        private static readonly int IdForwardFade = Shader.PropertyToID("_ForwardFade");
        private static readonly int IdMinHeightFactor = Shader.PropertyToID("_MinHeightFactor");
        private static readonly int IdFadeStart = Shader.PropertyToID("_FadeStart");
        private static readonly int IdMinAlpha = Shader.PropertyToID("_MinAlpha");
        private static readonly int IdTintStrength = Shader.PropertyToID("_TintStrength");
        private static readonly int IdTintColorA = Shader.PropertyToID("_TintColorA");
        private static readonly int IdTintColorB = Shader.PropertyToID("_TintColorB");

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void OnValidate()
        {
            if (EdgeFalloff < 0f) EdgeFalloff = 0f;
            if (MinHalfWidth < 0f) MinHalfWidth = 0f;
            if (MaxDistance < 1f) MaxDistance = 1f;
        }

        private void LateUpdate()
        {
            if (Player == null) return;

            Transform cam = CameraTransform != null ? CameraTransform
                          : (Camera.main != null ? Camera.main.transform : null);
            if (cam == null) return;

            Vector3 pp = Player.position;
            Vector3 cp = cam.position;

            Vector2 dir = new Vector2(cp.x - pp.x, cp.z - pp.z);
            float horizontalDist = dir.magnitude; // player->camera distance in the XZ plane
            if (dir.sqrMagnitude < 1e-4f)
            {
                Vector3 f = cam.forward;
                dir = new Vector2(f.x, f.z);
                if (dir.sqrMagnitude < 1e-6f) dir = Vector2.up;
            }
            dir.Normalize();
            Vector2 side = new Vector2(-dir.y, dir.x);

            // Effective cone reach: either tied to the camera's horizontal distance (so the cone
            // always reaches the camera at any zoom) or the fixed MaxDistance.
            float effectiveMaxDistance = MaxDistanceFromCamera
                ? Mathf.Max(1f, horizontalDist * MaxDistanceScale)
                : MaxDistance;

            float tanHalf = Mathf.Tan(ConeHalfAngle * Mathf.Deg2Rad);

            Shader.SetGlobalVector(IdPlayerXZ, new Vector4(pp.x, pp.z, 0f, 0f));
            Shader.SetGlobalVector(IdConeDirXZ, new Vector4(dir.x, dir.y, 0f, 0f));
            Shader.SetGlobalVector(IdConeSideXZ, new Vector4(side.x, side.y, 0f, 0f));
            Shader.SetGlobalFloat(IdMaxDistance, effectiveMaxDistance);
            Shader.SetGlobalFloat(IdTanHalfAngle, tanHalf);
            Shader.SetGlobalFloat(IdMinHalfWidth, MinHalfWidth);
            Shader.SetGlobalFloat(IdEdgeFalloff, EdgeFalloff);
            Shader.SetGlobalFloat(IdForwardFade, ForwardFade);
            Shader.SetGlobalFloat(IdMinHeightFactor, MinHeightFactor);
            Shader.SetGlobalFloat(IdFadeStart, FadeStart);
            Shader.SetGlobalFloat(IdMinAlpha, MinAlpha);
            Shader.SetGlobalFloat(IdTintStrength, TintStrength);
            Shader.SetGlobalColor(IdTintColorA, TintColorA);
            Shader.SetGlobalColor(IdTintColorB, TintColorB);
        }

        private void OnDrawGizmos()
        {
            if (!DebugGizmos || !Application.isPlaying || Player == null) return;
            Transform cam = CameraTransform != null ? CameraTransform
                          : (Camera.main != null ? Camera.main.transform : null);
            if (cam == null) return;

            Vector3 pp = Player.position;
            Vector3 cp = cam.position;
            Vector2 d2 = new Vector2(cp.x - pp.x, cp.z - pp.z);
            float horizontalDist = d2.magnitude;
            if (d2.sqrMagnitude < 1e-4f)
            {
                Vector3 f = cam.forward;
                d2 = new Vector2(f.x, f.z);
            }
            d2.Normalize();
            Vector3 dir = new Vector3(d2.x, 0f, d2.y);
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);

            float maxDist = MaxDistanceFromCamera
                ? Mathf.Max(1f, horizontalDist * MaxDistanceScale)
                : MaxDistance;

            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(pp, cp);

            // Ball around the player (the swept-sphere's near cap).
            Gizmos.color = new Color(0f, 1f, 1f, 0.5f);
            Gizmos.DrawWireSphere(pp, MinHalfWidth);

            float tanHalf = Mathf.Tan(ConeHalfAngle * Mathf.Deg2Rad);
            Vector3 origin = pp;
            Vector3 farCenter = origin + dir * maxDist;
            float farHalf = Mathf.Max(MinHalfWidth, maxDist * tanHalf);

            Vector3 nearL = origin + side * MinHalfWidth;
            Vector3 nearR = origin - side * MinHalfWidth;
            Vector3 farL = farCenter + side * farHalf;
            Vector3 farR = farCenter - side * farHalf;

            Gizmos.color = new Color(0f, 1f, 1f, 0.7f);
            Gizmos.DrawLine(nearL, farL);
            Gizmos.DrawLine(nearR, farR);
            Gizmos.DrawLine(nearL, nearR);
            Gizmos.DrawLine(farL, farR);
            Gizmos.color = new Color(1f, 1f, 0f, 0.5f);
            Gizmos.DrawLine(origin, farCenter);
        }
    }
}