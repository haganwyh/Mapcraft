using System;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Utilities;
using Mapbox.LocationModule;
using UnityEngine;

namespace Mapbox.Examples
{
    /// <summary>
    /// GPS-driven slippy-map positioning with SMOOTH motion. The player transform stays
    /// PINNED (never moves horizontally). Real device GPS fixes set a TARGET map center;
    /// each frame the actual map center EASES toward that target, so the world glides
    /// underneath the fixed player instead of snapping fix-to-fix.
    ///
    /// WHY FINE GPS SETTINGS ARE CLAIMED IN AWAKE (the "only moves every few meters" fix)
    /// Unity's parameterless Input.location.Start() defaults to desiredAccuracy 10 m and a
    /// 10 m UPDATE DISTANCE FILTER: the OS withholds new fixes until the device has moved
    /// ~10 m. The Mapbox v3.1 LocationProviderFactory starts the service with exactly that
    /// parameterless overload, BUT only `if (status != Running)` — whoever starts the
    /// service FIRST owns its settings. Every Awake runs before any Start, so this script
    /// claims the service in Awake with fine settings and everything downstream inherits.
    ///
    /// WALK ANIMATION: EVIDENCE + HOLD TIMER (the "stuttering animation" fix)
    /// GPS fixes arrive every 1..several seconds; the map-center ease finishes well before
    /// the next fix. Keying the animation to "is the center still easing" therefore plays
    /// for ~a second and idles until the next fix — a stutter. Instead, the animation is
    /// keyed to EVIDENCE OF WALKING WITH MEMORY: every accepted fix that shows real
    /// displacement (beyond jitter) stamps a time, and the walk animation stays on while
    /// that stamp is younger than WalkingHoldSeconds. Fix gaps shorter than the hold are
    /// bridged seamlessly; stand still and the evidence dries up, so the animation stops
    /// once, a moment after you do — which reads as follow-through, not error.
    ///
    /// SETUP
    ///  - Add a LocationProviderFactory to the scene; assign it to the map's
    ///    LocationFactory field AND to this script's LocationFactory field. On device set
    ///    its type to "Unity Location Provider".
    ///  - Android: ACCESS_FINE_LOCATION. iOS: NSLocationWhenInUseUsageDescription.
    /// </summary>
    public class PlayerMovement : MonoBehaviour
    {
        [Tooltip("The map. The map CENTER is what follows GPS.")]
        public MapBehaviourCore MapBehaviour;

        [Tooltip("The location provider factory in the scene. Owns the location provider " +
                 "and emits OnLocationUpdated with the device's lat/lon.")]
        public LocationProviderFactory LocationFactory;

        public Animator CharacterAnimator;

        [Header("GPS precision")]
        [Tooltip("Requested positioning accuracy in meters. 1 requests the platform's " +
                 "best (GPS) accuracy. Higher values save battery but coarsen fixes.")]
        public float DesiredAccuracyMeters = 1f;

        [Tooltip("Minimum movement (meters) before the OS reports a new fix. SMALL VALUES " +
                 "ARE THE POINT: Unity's default of 10 is why movement only updated every " +
                 "few meters. 0.1-0.5 gives continuous Google-Maps-like updates.")]
        public float UpdateDistanceMeters = 0.1f;

        [Tooltip("If some other system already started the location service with coarse " +
                 "defaults, stop it and restart with the fine settings above.")]
        public bool RestartCoarseService = true;

        [Header("Smoothing")]
        [Tooltip("How quickly the map center catches up to the latest GPS fix. Higher = " +
                 "snappier (closer to teleporting), lower = floatier. ~3-6 feels natural " +
                 "for walking. Frame-rate independent exponential ease.")]
        public float FollowSharpness = 4f;

        [Tooltip("If the gap to the target center exceeds this many meters, jump instantly " +
                 "instead of easing. Stops a long lerp after a big GPS correction or the " +
                 "first fix. 0 = never jump (always ease).")]
        public float TeleportIfFartherThanMeters = 50f;

        [Header("Walk animation")]
        [Tooltip("How long the walk animation persists after the last evidence of real " +
                 "movement. Set slightly longer than your worst-case gap between GPS fixes " +
                 "while walking (2-4 s typical). This is what bridges the fix gaps.")]
        public float WalkingHoldSeconds = 3f;

        [Tooltip("A fix must displace at least this many meters from the previous accepted " +
                 "fix to count as evidence of walking. Filters stationary GPS jitter. Keep " +
                 "below (walking speed x fix interval): ~0.6-1.0 m.")]
        public float MinMovementForWalkMeters = 0.8f;

        [Tooltip("Optional Animator float parameter to receive the estimated ground speed " +
                 "in m/s (for blend trees / playback rate). Leave empty to skip.")]
        public string SpeedParameterName = "";

        [Header("Facing / Filtering")]
        [Tooltip("Rotate the player to face the GPS travel heading. LEAVE OFF if " +
                 "PlayerCompass drives the facing.")]
        public bool FacePlayerInTravelHeading = false;

        [Tooltip("Smoothing for the facing rotation, degrees-ease per second. 0 = snap.")]
        public float HeadingTurnSharpness = 6f;

        [Tooltip("Fixes with worse horizontal accuracy (meters) than this are ignored. " +
                 "0 = accept every fix.")]
        public float MaxAcceptableAccuracyMeters = 0f;

        [Tooltip("Snap the player's Y to the terrain elevation under the map center.")]
        public bool SnapToTerrain = false;

        [Tooltip("Gaps smaller than this many meters are treated as arrived: the per-frame " +
                 "ChangeView/redraw is skipped. Purely a map-redraw optimisation now — the " +
                 "walk animation no longer keys off this.")]
        public float StopAnimationWithinMeters = 0.5f;

        private MapboxMap _map;
        private IMapInformation _mapInformation;
        private ILocationProvider _locationProvider;
        private bool _mapReady;
        private bool _subscribed;

        // Smoothing state, all in Web Mercator.
        private bool _hasTarget;
        private Vector2d _targetMercator;
        private float _targetHeadingDeg;
        private bool _hasHeading;

        // Walk-evidence state.
        private bool _hasPrevFix;
        private Vector2d _prevFixMercator;
        private float _prevFixTime;
        private float _lastMovementEvidenceTime = float.NegativeInfinity;
        private float _estimatedSpeed;      // m/s, smoothed
        private bool _isWalking;

        private void Awake()
        {
            // Claim the location service before anyone else can (factory and compass both
            // start it from Start-phase coroutines; Awake always runs first).
            var status = Input.location.status;
            if (status == LocationServiceStatus.Stopped ||
                status == LocationServiceStatus.Failed)
            {
                Input.location.Start(DesiredAccuracyMeters, UpdateDistanceMeters);
            }
            else if (RestartCoarseService && status == LocationServiceStatus.Running)
            {
                Input.location.Stop();
                Input.location.Start(DesiredAccuracyMeters, UpdateDistanceMeters);
            }
        }

        private void Start()
        {
            if (MapBehaviour == null || LocationFactory == null)
            {
                Debug.LogError("PlayerMovement: assign both MapBehaviour and LocationFactory.");
                enabled = false;
                return;
            }

            MapBehaviour.Initialized += map =>
            {
                _map = map;
                _mapInformation = map.MapInformation;
                _mapReady = true;
                TrySubscribe();
            };

            if (LocationFactory.IsLocationProviderReady)
                TrySubscribe();
            LocationFactory.OnLocationProviderReady += _ => TrySubscribe();
        }

        private void TrySubscribe()
        {
            if (_subscribed || !_mapReady) return;

            _locationProvider = LocationFactory.DefaultLocationProvider;
            if (_locationProvider == null) return;

            _locationProvider.OnLocationUpdated += OnLocationUpdated;
            _subscribed = true;

            if (_locationProvider.HasReceivedFirstFix)
                OnLocationUpdated(_locationProvider.CurrentLocation);
        }

        // GPS callback: record the target, and judge whether this fix is EVIDENCE of real
        // walking. The map move itself happens in Update so it's spread across frames.
        private void OnLocationUpdated(Location location)
        {
            if (MaxAcceptableAccuracyMeters > 0f &&
                location.Accuracy > MaxAcceptableAccuracyMeters)
                return;

            var fix = Conversions.LatitudeLongitudeToWebMercator(location.LatitudeLongitude);
            var now = Time.time;

            if (_hasPrevFix)
            {
                var stepMeters = MercatorGapInMeters(fix - _prevFixMercator);
                var dt = Mathf.Max(0.25f, now - _prevFixTime);

                // A displacement beyond jitter is what "walking" means here. Teleport-scale
                // jumps are corrections, not walks, so they don't count as evidence.
                var isTeleportScale = TeleportIfFartherThanMeters > 0f &&
                                      stepMeters > TeleportIfFartherThanMeters;
                if (!isTeleportScale && stepMeters >= MinMovementForWalkMeters)
                {
                    _lastMovementEvidenceTime = now;

                    // Speed estimate for the optional animator parameter, lightly smoothed.
                    var rawSpeed = (float)(stepMeters / dt);
                    _estimatedSpeed = Mathf.Lerp(_estimatedSpeed, rawSpeed, 0.5f);
                }
            }

            _prevFixMercator = fix;
            _prevFixTime = now;
            _hasPrevFix = true;

            _targetMercator = fix;
            _hasTarget = true;

            if (FacePlayerInTravelHeading && location.IsUserHeadingUpdated)
            {
                _targetHeadingDeg = location.UserHeading;
                _hasHeading = true;
            }
        }

        private void Update()
        {
            UpdateWalkAnimation();

            if (!_hasTarget || _map == null ||
                _map.Status < InitializationStatus.ReadyForUpdates)
                return;

            var current = _mapInformation.CenterMercator;
            var gap = _targetMercator - current;
            double gapMeters = MercatorGapInMeters(gap);

            bool teleporting = TeleportIfFartherThanMeters > 0f &&
                               gapMeters > TeleportIfFartherThanMeters;

            // Arrived (and no teleport pending): skip the per-frame ChangeView/redraw.
            // NOTE this is purely a redraw optimisation — the walk animation is driven by
            // the evidence timer above, so returning here no longer stutters it.
            if (!teleporting && gapMeters <= StopAnimationWithinMeters)
                return;

            float t = 1f - Mathf.Exp(-FollowSharpness * Time.deltaTime);

            Vector2d nextMercator;
            if (teleporting)
            {
                nextMercator = _targetMercator; // big correction: snap, don't crawl
                // A teleport invalidates walking evidence — it was a correction, not a walk.
                _lastMovementEvidenceTime = float.NegativeInfinity;
            }
            else
            {
                nextMercator = current + gap * t;
            }

            var nextLatLng = Conversions.WebMercatorToLatLon(nextMercator);
            _map.ChangeView(nextLatLng);

            if (FacePlayerInTravelHeading && _hasHeading)
            {
                var targetRot = Quaternion.Euler(0f, _targetHeadingDeg, 0f);
                transform.rotation = HeadingTurnSharpness > 0f
                    ? Quaternion.Slerp(transform.rotation, targetRot,
                        1f - Mathf.Exp(-HeadingTurnSharpness * Time.deltaTime))
                    : targetRot;
            }

            if (SnapToTerrain)
            {
                var centerLatLng = _mapInformation.LatitudeLongitude;
                var tileId = Conversions.LatitudeLongitudeToTileId(centerLatLng, 16).Canonical;
                var tileSpace = Conversions.LatitudeLongitudeToInTile01(centerLatLng, tileId);
                var elevation = _mapInformation.QueryElevation(tileId, tileSpace.x, tileSpace.y);
                var p = transform.position;
                transform.position = new Vector3(p.x, elevation, p.z);
            }
        }

        /// <summary>
        /// Walking = the last evidence of real movement is younger than the hold window.
        /// This is what bridges the gaps between sparse GPS fixes: the animation keeps
        /// playing through a gap and stops once, shortly after the player actually stops.
        /// </summary>
        private void UpdateWalkAnimation()
        {
            var walking = (Time.time - _lastMovementEvidenceTime) <= WalkingHoldSeconds;

            if (CharacterAnimator)
            {
                if (walking != _isWalking)
                    CharacterAnimator.SetBool("IsWalking", walking);

                if (!string.IsNullOrEmpty(SpeedParameterName))
                {
                    // Decay the reported speed to zero while idle so blend trees settle.
                    var speed = walking ? _estimatedSpeed : 0f;
                    CharacterAnimator.SetFloat(SpeedParameterName, speed);
                }
            }

            _isWalking = walking;
        }

        // Approximate ground-meters length of a Web Mercator delta at the current
        // latitude (Mercator meters are stretched by 1/cos(lat) away from the equator).
        private double MercatorGapInMeters(Vector2d mercatorDelta)
        {
            double lat = _mapInformation != null
                ? _mapInformation.LatitudeLongitude.Latitude
                : 0.0;
            double scale = Math.Cos(lat * Math.PI / 180.0);
            return mercatorDelta.magnitude * scale;
        }

        private void OnDestroy()
        {
            if (_subscribed && _locationProvider != null)
                _locationProvider.OnLocationUpdated -= OnLocationUpdated;
        }
    }
}