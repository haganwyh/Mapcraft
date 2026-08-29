using System;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Utilities;
using UnityEngine;

namespace Mapbox.Example.Scripts.MapInput
{
    [Serializable]
    public class DollyMapCamera : MapInput
    {
        // Compile-time platform split, mirroring the one in MapInput.cs. On device
        // builds the player is fixed and the map follows; one finger must ROTATE (no
        // pan), since touch has no secondary button. In the Editor / on desktop the
        // mouse drives the classic scheme: left-drag pans, right-drag rotates.
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        private const bool IsTouchPlatform = true;
#else
        private const bool IsTouchPlatform = false;
#endif

        public enum RotationMode
        {
            RotateTheCamera,
            RotateTheMap
        }

        [Serializable]
        private class RotationSettings
        {
            [Tooltip("Enable rotation. Desktop: right-click drag. Mobile: single-finger drag.")]
            public bool Enabled = true;
            [Tooltip("Rotation sensitivity. Degrees of rotation per pixel of pointer movement. " +
                     "Default: 0.8. (This is now frame-rate independent — it is NOT multiplied by " +
                     "Time.deltaTime — so a lag spike no longer causes a rotation jump.)")]
            public float Speed = 0.8f;
            [Tooltip("Sensitivity for single-finger rotation on mobile, in degrees per pixel. " +
                     "Kept separate from Speed because a touch drag usually wants a gentler " +
                     "response than a mouse drag. Default: 0.2.")]
            public float TouchSpeed = 0.2f;
            [Tooltip("Safety clamp: the most degrees a single frame's drag can rotate. Stops a " +
                     "lag spike (where many frames' pointer movement collapses into one large " +
                     "delta) from spinning the view wildly. 0 = no clamp.")]
            public float MaxDegreesPerFrame = 30f;
            [Tooltip("RotateTheCamera: camera orbits the map. RotateTheMap: map rotates under a fixed camera")]
            public RotationMode rotationMode;
            [Tooltip("Root transform of the map. Auto-detected from MapBehaviourCore if left empty")]
            public Transform MapRoot;
        }

        [Serializable]
        private class ZoomSettings
        {
            [Tooltip("Enable scroll wheel / pinch to zoom in/out")]
            public bool Enabled = true;
            [Tooltip("Zoom sensitivity for the desktop scroll wheel, per scroll step. Default: 0.25")]
            public float Speed = 0.25f;
            [Tooltip("Zoom sensitivity for mobile pinch. Kept separate from Speed because the " +
                     "pinch delta is normalized differently than a scroll notch and usually wants " +
                     "a punchier response. Default: 1.0.")]
            public float TouchSpeed = 1.0f;
            [Tooltip("When enabled, zoom centers on cursor position. When disabled, zoom centers on map center")]
            public bool ZoomAtCursor = true;
        }

        [Tooltip("Initial map center in latitude/longitude. Example: 40.7128, -74.0060 for New York")]
        public LatitudeLongitude CenterLatitudeLongitude;
        [Tooltip("Camera tilt angle. 90 = top-down, 15 = near-horizon")]
        [Range(15, 90)]
        public float Pitch;
        [Tooltip("Camera rotation in degrees. 0 = north up")]
        [Range(-180, 180)]
        public float Bearing;
        [Tooltip("Distance from ground target to camera along its forward axis. Higher = further away. Typical range: 100–1000 depending on scale")]
        public float CameraDistance = 200f;
        [Tooltip("The fixed player/target the camera dollies toward when zooming")]
        public Transform ZoomTarget;
        [Tooltip("Closest the camera can get to the zoom target")]
        public float MinCameraDistance = 50f;
        [Tooltip("Furthest the camera can get from the zoom target")]
        public float MaxCameraDistance = 1000f;

        [Tooltip("Pushes the target (player) DOWN on screen for a better forward view. " +
                 "0 = player centered. 0.2 = player sits ~20% of the screen height below center. " +
                 "Negative pushes the player up. Works by aiming the camera at a point ahead of " +
                 "the player along the ground, so it scales naturally with pitch and distance.")]
        [Range(-0.45f, 0.45f)]
        public float VerticalScreenOffset = 0.2f;

        [Header("Pitch Limits")]
        [Tooltip("Minimum allowed pitch angle (closer to the horizon).")]
        [Range(5, 90)]
        public float MinPitch = 15f;

        [Tooltip("Maximum allowed pitch angle (90 = straight down top-down view).")]
        [Range(5, 90)]
        public float MaxPitch = 90f;

        [NonSerialized] public float ZoomValue;
        [NonSerialized] public float ScaleValue;

        private float _initialZoom;
        private float _initialScale;

        [Tooltip("Enable left-click drag to pan the map. Desktop only — on mobile the map " +
                 "follows the fixed player and a single finger rotates instead, so panning is " +
                 "always off on device regardless of this flag.")]
        public bool PanEnabled = true;

        [Tooltip("Camera rotation settings")]
        [SerializeField] private RotationSettings _rotationSettings;
        [Tooltip("Zoom behaviour settings")]
        [SerializeField] private ZoomSettings _zoomSettings;
        private float _initialPitch;

        private Vector3 _previousScreenPosition;
        private Vector3 _dragOrigin;
        private Vector3 _targetPosition;

        public override void Initialize(Camera camera, IMapInformation start)
        {
            Initialize(camera, start, null);
        }

        public void Initialize(Camera camera, IMapInformation start, Plane? controlPlane)
        {
            base.Initialize(camera, start);
            if (controlPlane.HasValue) _controlPlane = controlPlane.Value;
            if (_rotationSettings.MapRoot == null)
            {
                var foundRoot = GameObject.FindObjectOfType<MapBehaviourCore>();
                if (foundRoot == null)
                {
                    Debug.LogError("DollyMapCamera.Initialize: rotationSettings.MapRoot is unset and no MapBehaviourCore could be found in the scene. Camera rotation will not work; assign a MapRoot explicitly.");
                    return;
                }
                _rotationSettings.MapRoot = foundRoot.transform;
            }
            Pitch = start.Pitch;
            Bearing = start.Bearing;
            _initialZoom = start.Zoom;
            _initialScale = start.Scale;
            _initialPitch = Pitch;
            ZoomValue = start.Zoom;
            ScaleValue = start.Scale;
            SetCamPositionByMapInfo();
            start.SetView += SetCamera;
        }

        public override void Teardown(IMapInformation mapInfo)
        {
            if (mapInfo != null) mapInfo.SetView -= SetCamera;
        }

        private const float MaxMercatorLatitude = 85.06f;

        public override CameraOutput UpdateCamera(IMapInformation mapInformation)
        {
            _output.Reset();
            UpdateInputState();

            if (IsPointerOverUI())
                return _output;

            var pointerPos = GetPointerPosition();
            Vector3 cursorHit;
            Vector2d newMercatorCenter = mapInformation.CenterMercator;

            if (!GetPlaneIntersection(pointerPos, out cursorHit))
                return _output;

            // Reset drag anchors on a fresh press, or when a touch lifts mid-gesture
            // (2→1) so the surviving finger's first frame doesn't read a huge delta.
            if (GetPointerDown() || GetSecondaryDown() || TouchCountDecreasedThisFrame)
            {
                _previousScreenPosition = pointerPos;
                _dragOrigin = cursorHit;
            }

            // ---- Primary single-pointer drag -------------------------------------
            // GetPointerHeld() is a left-mouse drag on desktop and a SINGLE-finger
            // drag on touch (the touch handler returns true only when TouchCount == 1).
            // Desktop -> pan the map. Mobile -> rotate (player is fixed, no panning).
            if (GetPointerHeld())
            {
                if (IsTouchPlatform)
                {
                    if (_rotationSettings.Enabled)
                        ApplyRotationFromScreenDelta(pointerPos, _rotationSettings.TouchSpeed);
                }
                else if (PanEnabled)
                {
                    var oldCursorLatLng = Conversions.LatitudeLongitudeToWebMercator(mapInformation.ConvertPositionToLatLng(_rotationSettings.MapRoot.InverseTransformPoint(_dragOrigin)));
                    var newCursorLatLng = Conversions.LatitudeLongitudeToWebMercator(mapInformation.ConvertPositionToLatLng(_rotationSettings.MapRoot.InverseTransformPoint(cursorHit)));
                    var mercatorDelta = newCursorLatLng - oldCursorLatLng;
                    if (mercatorDelta.x != 0 || mercatorDelta.y != 0)
                    {
                        newMercatorCenter = mapInformation.CenterMercator - mercatorDelta;
                        _output.HasChanged = true;
                    }
                }
            }
            // ---- Secondary drag (desktop only) -----------------------------------
            // Right-mouse rotate. Dormant on touch (GetSecondaryHeld() is always false
            // on the touch handler), and single-finger rotation above replaces it.
            else if (GetSecondaryHeld() && _rotationSettings.Enabled)
            {
                ApplyRotationFromScreenDelta(pointerPos, _rotationSettings.Speed);
            }
            // ---- Pinch zoom (touch) / scroll zoom (desktop) ----------------------
            else if (GetPinchZoomDelta(out var zoomDelta) && _zoomSettings.Enabled)
            {
                if (zoomDelta != 0)
                {
                    float zoomSpeed = IsTouchPlatform ? _zoomSettings.TouchSpeed : _zoomSettings.Speed;
                    CameraDistance = Mathf.Clamp(
                        CameraDistance - zoomDelta * zoomSpeed * CameraDistance,
                        MinCameraDistance,
                        MaxCameraDistance);
                    _output.HasChanged = true;
                }
            }

            // Two-finger vertical drag tilts the pitch (touch only).
            if (GetTwoFingerTiltDelta(out var tiltDelta))
            {
                Pitch -= tiltDelta * _rotationSettings.Speed;
                Pitch = Mathf.Clamp(Pitch, MinPitch, MaxPitch);
                _output.HasChanged = true;
            }

            _dragOrigin = cursorHit;
            _previousScreenPosition = pointerPos;

            if (_output.HasChanged)
            {
                var latLng = Conversions.WebMercatorToLatLon(newMercatorCenter);
                CenterLatitudeLongitude = new LatitudeLongitude(
                    System.Math.Clamp(latLng.Latitude, -(double)MaxMercatorLatitude, MaxMercatorLatitude),
                    latLng.Longitude);
                SetCamPositionByMapInfo();
                _output.Center = CenterLatitudeLongitude;
                _output.Zoom = ZoomValue;
                _output.Pitch = Pitch;
                _output.Bearing = Bearing;
                _output.Scale = ScaleValue;
            }

            return _output;
        }

        // Shared rotation from a per-frame screen-space pointer delta. Used by both
        // the desktop right-drag and the mobile single-finger drag. The delta is
        // already a per-frame quantity (how far the pointer moved since last frame),
        // so it must NOT be multiplied by Time.deltaTime — doing so made rotation
        // frame-rate dependent and, on a lag spike (Time.deltaTime balloons while
        // several frames of pointer movement collapse into one large delta), produced
        // a dramatic rotation jump. The pixel delta is inherently frame-rate
        // independent. MaxDegreesPerFrame caps a single collapsed-frame delta.
        private void ApplyRotationFromScreenDelta(Vector3 pointerPos, float speed)
        {
            var deltaMousePos = pointerPos - _previousScreenPosition;
            var deltaAngleH = deltaMousePos.x;
            var deltaAngleV = deltaMousePos.y;
            if (deltaAngleH == 0 && deltaAngleV == 0)
                return;

            float pitchDelta = deltaAngleV * speed;
            float bearingDelta = deltaAngleH * speed;

            if (_rotationSettings.MaxDegreesPerFrame > 0f)
            {
                float maxStep = _rotationSettings.MaxDegreesPerFrame;
                pitchDelta = Mathf.Clamp(pitchDelta, -maxStep, maxStep);
                bearingDelta = Mathf.Clamp(bearingDelta, -maxStep, maxStep);
            }

            Pitch -= pitchDelta;
            Pitch = Mathf.Clamp(Pitch, MinPitch, MaxPitch);

            Bearing = ClampBearing(Bearing + bearingDelta);

            _output.HasChanged = true;
        }

        private void SetCamPositionByMapInfo()
        {
            Vector3 pivot = ZoomTarget != null ? ZoomTarget.position : _targetPosition;

            if (_rotationSettings.rotationMode == RotationMode.RotateTheCamera)
            {
                _camera.transform.rotation = Quaternion.Euler(Pitch, Bearing, 0);
            }
            else if (_rotationSettings.rotationMode == RotationMode.RotateTheMap)
            {
                var projectedForward = Vector3.ProjectOnPlane(_camera.transform.forward, _controlPlane.normal);
                var verticalRotation = Quaternion.AngleAxis(Pitch - _initialPitch, projectedForward.Perpendicular());
                _rotationSettings.MapRoot.rotation = verticalRotation * Quaternion.Euler(0, Bearing, 0);
            }

            Vector3 aimPoint = pivot + ComputeAimOffset();
            _camera.transform.position = aimPoint - _camera.transform.forward * CameraDistance;
        }

        private Vector3 ComputeAimOffset()
        {
            if (Mathf.Approximately(VerticalScreenOffset, 0f) || _camera == null)
                return Vector3.zero;

            Vector3 groundForward = Vector3.ProjectOnPlane(_camera.transform.forward, _controlPlane.normal);
            if (groundForward.sqrMagnitude < 1e-6f)
                return Vector3.zero;
            groundForward.Normalize();

            float halfFovRad = Mathf.Deg2Rad * _camera.fieldOfView * 0.5f;
            float viewHalfHeight = CameraDistance * Mathf.Tan(halfFovRad);
            float screenShift = VerticalScreenOffset * 2f * viewHalfHeight;

            float pitchRad = Mathf.Deg2Rad * Mathf.Clamp(Pitch, Mathf.Max(5f, MinPitch), 90f);
            float groundFactor = Mathf.Max(0.2f, Mathf.Sin(pitchRad));
            float groundShift = screenShift / groundFactor;

            return groundForward * groundShift;
        }

        public void SetCamera(IMapInformation mapInfo)
        {
            _targetPosition = Vector3.zero;
            Pitch = mapInfo.Pitch;
            Bearing = mapInfo.Bearing;

            if (_rotationSettings.rotationMode == RotationMode.RotateTheCamera)
            {
                SetCamPositionByMapInfo();
            }
            else if (_rotationSettings.rotationMode == RotationMode.RotateTheMap)
            {
                var projectedForward = Vector3.ProjectOnPlane(_camera.transform.forward, _controlPlane.normal);
                var verticalRotation = Quaternion.AngleAxis(Pitch - _initialPitch, projectedForward.Perpendicular());
                _rotationSettings.MapRoot.rotation = verticalRotation * Quaternion.Euler(0, Bearing, 0);
            }
        }
    }
}