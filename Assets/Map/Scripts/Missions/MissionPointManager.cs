using System;
using System.Collections.Generic;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Utilities;
using Mapbox.Example.Scripts.Map;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Events;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.AsyncOperations;
#endif

namespace Mapbox.Missions
{
    /// <summary>
    /// Shows mission points on the map as 3D markers within a radius of the player, and
    /// triggers them on tap when the player is close enough.
    ///
    /// WHY THIS IS NOT BUILT LIKE THE FLOWER FIELD
    /// Flowers are anonymous, generated, and counted in thousands, so they get a seeded cell
    /// grid and GPU instancing. Missions are AUTHORED DATA WITH IDENTITY: dozens of them, each
    /// with a stable id, a photo, and completion state. They cannot be regenerated from a seed
    /// and they need colliders to be tapped. So: real GameObjects, positioned straight from
    /// lat/lon, pooled by id.
    ///
    /// Distances are computed as real ground metres via haversine on lat/lon, NOT in Unity
    /// units — that stays correct regardless of MapInformation.Scale, zoom or latitude.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class MissionPointManager : MonoBehaviour
    {
        #region Inspector

        [Header("Wiring")]
        public MapBehaviourCore MapCore;

        [Tooltip("The avatar. On a slippy map it sits at world origin and the map moves under it.")]
        public Transform Player;

        [Tooltip("Camera used for tap raycasts and billboarding. Empty = Camera.main.")]
        public Camera Camera;

        [Tooltip("Marker prefab. Needs a MissionMarker component and a Collider.")]
        public MissionMarker MarkerPrefab;

        [Header("Data")]
        [Tooltip("JSON file of missions. Format: { \"missions\": [ { \"id\": \"...\", " +
                 "\"latitude\": 22.298, \"longitude\": 114.170, \"title\": \"...\", " +
                 "\"photoKey\": \"...\" } ] }")]
        public TextAsset MissionJson;

        /*
        [Tooltip("Resolves photoKey to a Sprite. Create via Assets > Create > Mapbox > Missions " +
                 "> Photo Library. Swap for a downloader implementation when missions come " +
                 "from a server.")]
        public MissionPhotoProvider PhotoProvider;
        */

        [Header("Ranges (real ground metres)")]
        [Tooltip("Markers exist within this distance of the player.")]
        public float VisibleRadiusMetres = 300f;

        [Tooltip("Extra distance before a marker is removed, so a marker at the boundary " +
                 "doesn't flicker as GPS jitters.")]
        public float VisibleHysteresisMetres = 25f;

        [Tooltip("Default distance within which a mission can be triggered. A mission's own " +
                 "interactRadiusMetres overrides this when non-zero.")]
        public float InteractRadiusMetres = 30f;

        [Header("Placement")]
        [Tooltip("Sample terrain elevation so markers sit on the ground rather than at Y=0.")]
        public bool SnapToTerrain = false;

        [Tooltip("Height above ground, in Unity local units.")]
        public float HeightOffset = 0f;

        [Header("Interaction")]
        [Tooltip("Layers the tap raycast considers. Put markers on their own layer so taps " +
                 "can't be blocked by buildings, and map geometry can't swallow them.")]
        public LayerMask MarkerLayers = ~0;

        [Tooltip("A press that moves further than this many pixels is a map DRAG, not a tap. " +
                 "Without this, panning the map over a marker would fire it.")]
        public float DragThresholdPixels = 20f;

        [Tooltip("A press held longer than this is not a tap.")]
        public float MaxTapSeconds = 0.6f;

        [Tooltip("Log a message when an out-of-range marker is tapped, rather than ignoring it.")]
        public bool ReportOutOfRangeTaps = true;

        [Header("Performance")]
        [Tooltip("Seconds between recomputes of which missions are in range.")]
        public float RefreshInterval = 0.25f;

        [Header("Events")]
        public UnityEvent<MissionPoint> OnMissionTriggered;
        public UnityEvent<MissionPoint> OnMissionTappedOutOfRange;

        /// <summary>C# equivalents of the UnityEvents, for code subscribers.</summary>
        public event Action<MissionPoint> MissionTriggered;
        public event Action<MissionPoint> MissionTappedOutOfRange;

        #endregion

        #region State

        private MapboxMap _map;
        private Transform _root;
        private IMissionSource _source;

        private readonly List<MissionPoint> _missions = new();
        private readonly Dictionary<string, MissionMarker> _active = new();
        private readonly Stack<MissionMarker> _pool = new();
        private readonly List<string> _removeBuffer = new();

        private float _nextRefresh;
        private bool _pressActive;
        private Vector2 _pressStart;
        private float _pressTime;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (MapCore == null)
            {
                Debug.LogError("[Missions] MapCore is not assigned.", this);
                enabled = false;
                return;
            }
            MapCore.Initialized += OnMapInitialized;
        }

        private void OnDestroy()
        {
            if (MapCore != null) MapCore.Initialized -= OnMapInitialized;
        }

        private void OnMapInitialized(MapboxMap map)
        {
            _map = map;

            // Swap this line for a server-backed IMissionSource later; nothing else changes.
            _source = new JsonMissionSource(MissionJson);
            _source.Load(list =>
            {
                _missions.Clear();
                if (list != null) _missions.AddRange(list);
                Debug.Log($"[Missions] Loaded {_missions.Count} mission point(s).");
            });
        }

        private void LateUpdate()
        {
            if (_map == null || _map.Status < InitializationStatus.ReadyForUpdates) return;
            if (!EnsureRoot()) return;

            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + Mathf.Max(0.05f, RefreshInterval);
                RefreshMarkers();
            }

            UpdateMarkerPositions();
            PollInput();
        }

        private bool EnsureRoot()
        {
            if (_root != null) return true;
            var mapRoot = _map.UnityContext.MapRoot;
            if (mapRoot == null) return false;

            var go = new GameObject("~MissionMarkersRoot");
            _root = go.transform;
            // worldPositionStays:false so markers compose with any transform on the map
            // hierarchy (AR anchor, rotated rig) instead of pinning to world space.
            _root.SetParent(mapRoot, false);
            return true;
        }

        #endregion

        #region Spawn / despawn

        private LatitudeLongitude PlayerLatLng()
        {
            var mapRoot = _map.UnityContext.MapRoot;
            Vector3 local = Player != null
                ? mapRoot.InverseTransformPoint(Player.position)
                : Vector3.zero;
            return _map.MapInformation.ConvertPositionToLatLng(local);
        }

        private void RefreshMarkers()
        {
            if (MarkerPrefab == null || _missions.Count == 0) return;

            var playerLatLng = PlayerLatLng();
            var cam = Camera != null ? Camera : UnityEngine.Camera.main;

            // Remove markers that have gone out of range (plus hysteresis).
            _removeBuffer.Clear();
            foreach (var kvp in _active)
            {
                var m = kvp.Value.Mission;
                if (m == null) { _removeBuffer.Add(kvp.Key); continue; }
                double d = HaversineMetres(playerLatLng.Latitude, playerLatLng.Longitude, m.latitude, m.longitude);
                if (d > VisibleRadiusMetres + VisibleHysteresisMetres)
                    _removeBuffer.Add(kvp.Key);
            }
            for (int i = 0; i < _removeBuffer.Count; i++) Despawn(_removeBuffer[i]);

            // Spawn markers that have come into range, and refresh in-range state on all.
            for (int i = 0; i < _missions.Count; i++)
            {
                var m = _missions[i];
                double d = HaversineMetres(playerLatLng.Latitude, playerLatLng.Longitude, m.latitude, m.longitude);

                if (d <= VisibleRadiusMetres && !_active.ContainsKey(m.id))
                    Spawn(m, cam);

                if (_active.TryGetValue(m.id, out var marker))
                    marker.SetInRange(d <= InteractRadiusFor(m));
            }
        }

        private void Spawn(MissionPoint mission, Camera cam)
        {
            MissionMarker marker = _pool.Count > 0 ? _pool.Pop() : null;
            if (marker == null)
            {
                marker = Instantiate(MarkerPrefab);
                if (marker.GetComponentInChildren<Collider>(true) == null)
                    Debug.LogWarning($"[Missions] Marker prefab '{MarkerPrefab.name}' has no Collider — " +
                                     "it can be seen but never tapped.", this);
            }

            marker.transform.SetParent(_root, false);
            marker.gameObject.SetActive(true);
            marker.name = $"Mission_{mission.id}";
            marker.Bind(mission, null, cam);

            /*
            // Photo arrives via callback so a future downloading provider needs no changes here.
            if (PhotoProvider != null)
            {
                string wantedId = mission.id;
                PhotoProvider.Request(mission.photoKey, sprite =>
                {
                    // The marker may have been recycled while a download was in flight.
                    if (marker != null && marker.Mission != null && marker.Mission.id == wantedId)
                        marker.SetPhoto(sprite);
                });
            }
            */

            string wantedId = mission.id;
            marker.markerImageHandle = Addressables.LoadAssetAsync<Sprite>(mission.photoKey);
            marker.markerImageHandle.Completed += (handle) =>
            {
                if (marker != null && marker.Mission != null && marker.Mission.id == wantedId)
                {
                    marker.SetPhoto(handle.Result);
                }
            };

            _active[mission.id] = marker;
            PositionMarker(marker);
        }

        private void Despawn(string id)
        {
            if (!_active.TryGetValue(id, out var marker)) return;
            _active.Remove(id);
            if (marker == null) return;

            // Release the specific asset handle tied to this marker
            if (marker.markerImageHandle.IsValid())
            {
                Addressables.Release(marker.markerImageHandle);
            }

            marker.gameObject.SetActive(false);
            marker.transform.SetParent(_root, false);
            _pool.Push(marker);
        }

        /// <summary>
        /// Re-converts each visible marker's lat/lon every frame. Unlike the flower field —
        /// where thousands of conversions forced a shared-root trick — dozens of markers make
        /// this free, and it keeps every marker exactly on its coordinate while the map eases
        /// toward each GPS fix.
        /// </summary>
        private void UpdateMarkerPositions()
        {
            if (_active.Count == 0) return;
            foreach (var kvp in _active) PositionMarker(kvp.Value);
        }

        private void PositionMarker(MissionMarker marker)
        {
            if (marker == null || marker.Mission == null) return;
            var latlng = new LatitudeLongitude(marker.Mission.latitude, marker.Mission.longitude);
            var pos = _map.MapInformation.ConvertLatLngToPosition(latlng);

            if (SnapToTerrain && _map.TryGetElevation(latlng, out var elevation))
                pos.y = elevation;
            pos.y += HeightOffset;

            // Map-LOCAL space: must be localPosition under MapRoot, never transform.position.
            marker.transform.localPosition = pos;
        }

        private float InteractRadiusFor(MissionPoint m) =>
            m.interactRadiusMetres > 0f ? m.interactRadiusMetres : InteractRadiusMetres;

        #endregion

        #region Input

        /// <summary>
        /// Tap detection with drag rejection. The map pans on drag, so a naive "pointer up over
        /// a marker fires it" would trigger missions whenever the player panned across one.
        /// A tap is a press that stays within DragThresholdPixels and ends within MaxTapSeconds.
        /// </summary>
        private void PollInput()
        {
            if (!ReadPointer(out bool down, out bool up, out Vector2 pos)) return;

            if (down)
            {
                // Ignore presses that started on UI — a button over the map is not a map tap.
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                {
                    _pressActive = false;
                    return;
                }
                _pressActive = true;
                _pressStart = pos;
                _pressTime = Time.unscaledTime;
                return;
            }

            if (up && _pressActive)
            {
                _pressActive = false;
                if (Vector2.Distance(pos, _pressStart) > DragThresholdPixels) return;  // drag
                if (Time.unscaledTime - _pressTime > MaxTapSeconds) return;            // long press
                TryTapAt(pos);
            }
        }

        private bool ReadPointer(out bool down, out bool up, out Vector2 pos)
        {
            down = up = false;
            pos = default;

#if ENABLE_INPUT_SYSTEM
            // Pointer.current unifies mouse and touchscreen, so one path covers editor and device.
            var pointer = Pointer.current;
            if (pointer == null) return false;
            down = pointer.press.wasPressedThisFrame;
            up = pointer.press.wasReleasedThisFrame;
            pos = pointer.position.ReadValue();
            return down || up;
#else
            down = Input.GetMouseButtonDown(0);
            up = Input.GetMouseButtonUp(0);
            pos = Input.mousePosition;
            return down || up;
#endif
        }

        private void TryTapAt(Vector2 screenPos)
        {
            var cam = Camera != null ? Camera : UnityEngine.Camera.main;
            if (cam == null) return;

            var ray = cam.ScreenPointToRay(screenPos);
            if (!Physics.Raycast(ray, out var hit, 10000f, MarkerLayers, QueryTriggerInteraction.Collide))
                return;

            var marker = hit.collider.GetComponentInParent<MissionMarker>();
            if (marker == null || marker.Mission == null) return;

            var playerLatLng = PlayerLatLng();
            var m = marker.Mission;
            double distance = HaversineMetres(playerLatLng.Latitude, playerLatLng.Longitude,
                                              m.latitude, m.longitude);

            if (distance <= InteractRadiusFor(m))
            {
                Debug.Log($"[Missions] TRIGGERED {m} at {distance:F1} m.");
                OnMissionTriggered?.Invoke(m);
                MissionTriggered?.Invoke(m);
            }
            else if (ReportOutOfRangeTaps)
            {
                Debug.Log($"[Missions] {m} is {distance:F1} m away — move within " +
                          $"{InteractRadiusFor(m):F0} m to trigger it.");
                OnMissionTappedOutOfRange?.Invoke(m);
                MissionTappedOutOfRange?.Invoke(m);
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Great-circle distance in metres. Used instead of Unity-space distance so ranges mean
        /// real metres whatever the map scale, zoom or latitude — the same reason the flower
        /// field measures its own metres-per-unit.
        /// </summary>
        private static double HaversineMetres(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0;
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        /// <summary>Replaces the mission set at runtime — the seam a server refresh would use.</summary>
        public void SetMissions(List<MissionPoint> missions)
        {
            _removeBuffer.Clear();
            foreach (var kvp in _active) _removeBuffer.Add(kvp.Key);
            for (int i = 0; i < _removeBuffer.Count; i++) Despawn(_removeBuffer[i]);

            _missions.Clear();
            if (missions != null) _missions.AddRange(missions);
            _nextRefresh = 0f;
        }

        #endregion

        #region Gizmos

        private void OnDrawGizmosSelected()
        {
            if (Player == null) return;
            Gizmos.color = Color.cyan;
            DrawCircle(Player.position, InteractRadiusMetres);
            Gizmos.color = new Color(0f, 1f, 1f, 0.3f);
            DrawCircle(Player.position, VisibleRadiusMetres);
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
