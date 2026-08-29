using UnityEngine;
using UnityEngine.UI;

namespace Mapbox.Missions
{
    public enum MarkerSizeMode
    {
        /// <summary>Constant size in world units — grows and shrinks with camera distance like a real object.</summary>
        FixedWorldSize,

        /// <summary>Constant fraction of screen height — always the same size to read and tap.</summary>
        ConstantScreenSize
    }

    /// <summary>
    /// Goes on the mission marker PREFAB. Handles billboarding, sizing, and displaying the
    /// mission's photo and title. The manager owns spawning and interaction; this owns looks.
    ///
    /// PREFAB REQUIREMENTS
    ///  * A Collider (a BoxCollider sized to the visible card is ideal) somewhere in the
    ///    prefab — without one the marker cannot be tapped.
    ///  * Optionally a world-space Canvas with an Image for the photo, or a SpriteRenderer.
    ///    Assign whichever you use below; both are supported, neither is required.
    /// </summary>
    [DisallowMultipleComponent]
    public class MissionMarker : MonoBehaviour
    {
        [Header("Display targets (assign what your prefab uses)")]
        [Tooltip("World-space UI Image for the photo. Use this if your prefab has a Canvas.")]
        public Image PhotoImage;

        [Tooltip("SpriteRenderer for the photo, if you're not using a Canvas.")]
        public SpriteRenderer PhotoSprite;

        [Tooltip("Optional title label. Accepts TextMeshProUGUI or UI Text via the component's " +
                 "own inspector — assign whichever your prefab uses.")]
        public Text TitleText;

        [Header("Billboard")]
        [Tooltip("Face the camera every frame. Yaw-only keeps the marker upright (recommended " +
                 "for a pitched map camera); full billboard also tilts to face a high camera.")]
        public bool YawOnly = true;

        [Header("Size")]
        public MarkerSizeMode SizeMode = MarkerSizeMode.ConstantScreenSize;

        [Tooltip("FixedWorldSize: multiplier on the prefab's authored scale.")]
        public float WorldSizeMultiplier = 1f;

        [Tooltip("ConstantScreenSize: marker height as a fraction of screen height (0.12 = 12%).")]
        [Range(0.01f, 0.6f)] public float ScreenHeightFraction = 0.12f;

        [Tooltip("Clamps for ConstantScreenSize, as multipliers on the authored scale. Stops a " +
                 "very near or very far marker becoming absurd.")]
        public float MinScaleMultiplier = 0.2f;
        public float MaxScaleMultiplier = 20f;

        [Header("In-range feedback")]
        [Tooltip("Optional object enabled only when the player is close enough to trigger this " +
                 "mission — a glow, ring, or 'tap me' hint.")]
        public GameObject InRangeIndicator;

        [Tooltip("Optional extra scale applied when in range, for a subtle pop.")]
        public float InRangeScaleBoost = 1.1f;

        public MissionPoint Mission { get; private set; }
        public bool IsInRange { get; private set; }

        private Camera _camera;
        private Vector3 _baseScale = Vector3.one;
        private float _referenceHeight = 1f;   // world height at authored scale, measured once
        private bool _measured;

        private void Awake()
        {
            _baseScale = transform.localScale;
        }

        public void Bind(MissionPoint mission, Sprite photo, Camera cam)
        {
            Mission = mission;
            _camera = cam;

            if (TitleText != null) TitleText.text = mission?.title ?? string.Empty;
            SetPhoto(photo);
            SetInRange(false);
        }

        /// <summary>Separate from Bind so a downloaded photo can arrive after the marker spawns.</summary>
        public void SetPhoto(Sprite photo)
        {
            if (PhotoImage != null)
            {
                PhotoImage.sprite = photo;
                PhotoImage.enabled = photo != null;
            }
            if (PhotoSprite != null)
            {
                PhotoSprite.sprite = photo;
                PhotoSprite.enabled = photo != null;
            }
        }

        public void SetInRange(bool inRange)
        {
            if (IsInRange == inRange && _measured) return;
            IsInRange = inRange;
            if (InRangeIndicator != null && InRangeIndicator.activeSelf != inRange)
                InRangeIndicator.SetActive(inRange);
        }

        private void LateUpdate()
        {
            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;

            EnsureMeasured();
            FaceCamera();
            ApplyScale();
        }

        private void FaceCamera()
        {
            if (YawOnly)
            {
                // Face the camera's horizontal direction only, so the card stays upright
                // regardless of camera pitch — the right look for a tilted map camera.
                Vector3 dir = transform.position - _camera.transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 1e-6f)
                    transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }
            else
            {
                transform.rotation = Quaternion.LookRotation(
                    transform.position - _camera.transform.position, Vector3.up);
            }
        }

        private void ApplyScale()
        {
            float boost = IsInRange ? Mathf.Max(0.01f, InRangeScaleBoost) : 1f;

            if (SizeMode == MarkerSizeMode.FixedWorldSize)
            {
                transform.localScale = _baseScale * (WorldSizeMultiplier * boost);
                return;
            }

            // Constant screen size: solve for the world height that projects to the requested
            // fraction of screen height at this distance, then express it as a scale multiple
            // of the measured reference height.
            float distance = Vector3.Distance(transform.position, _camera.transform.position);
            if (distance < 1e-3f) return;

            float worldHeightAtScreenFraction;
            if (_camera.orthographic)
                worldHeightAtScreenFraction = _camera.orthographicSize * 2f * ScreenHeightFraction;
            else
                worldHeightAtScreenFraction =
                    2f * distance * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * ScreenHeightFraction;

            float mult = worldHeightAtScreenFraction / Mathf.Max(1e-4f, _referenceHeight);
            mult = Mathf.Clamp(mult, MinScaleMultiplier, MaxScaleMultiplier) * boost;

            // The marker is parented under MapRoot, so divide out any scale on the map
            // hierarchy — otherwise a scaled map would break the screen-size guarantee.
            var parent = transform.parent;
            float parentScale = parent != null ? Mathf.Abs(parent.lossyScale.y) : 1f;
            if (parentScale > 1e-4f) mult /= parentScale;

            transform.localScale = _baseScale * mult;
        }

        /// <summary>
        /// Measures the marker's world height at its authored scale, once, so screen-size
        /// maths has a reference. Uses renderer bounds including children; falls back to 1.
        /// </summary>
        private void EnsureMeasured()
        {
            if (_measured) return;
            _measured = true;

            var renderers = GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) { _referenceHeight = 1f; return; }

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            float currentScaleY = Mathf.Abs(transform.lossyScale.y);
            float authoredScaleY = Mathf.Abs(_baseScale.y);
            if (currentScaleY < 1e-4f || authoredScaleY < 1e-4f) { _referenceHeight = 1f; return; }

            // bounds are at CURRENT scale; normalize back to the authored scale.
            _referenceHeight = Mathf.Max(1e-4f, bounds.size.y / currentScaleY * authoredScaleY);
        }
    }
}
