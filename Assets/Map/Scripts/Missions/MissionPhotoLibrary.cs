using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mapbox.Missions
{
    /// <summary>
    /// Editor-assigned photos, looked up by key.
    ///
    /// Keys rather than list indices: a JSON file that says "harbour_pier" survives someone
    /// reordering this list, and a missing entry names itself in the console instead of
    /// silently showing the wrong picture.
    /// </summary>
    [CreateAssetMenu(menuName = "Mapbox/Missions/Photo Library")]
    public class MissionPhotoLibrary : MissionPhotoProvider
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("Must match a mission's photoKey in the JSON.")]
            public string key;
            public Sprite sprite;
        }

        public List<Entry> Photos = new();

        [Tooltip("Shown when a mission's photoKey has no entry here. Optional.")]
        public Sprite FallbackSprite;

        private Dictionary<string, Sprite> _lookup;

        private void BuildLookup()
        {
            _lookup = new Dictionary<string, Sprite>();
            for (int i = 0; i < Photos.Count; i++)
            {
                var e = Photos[i];
                if (e == null || string.IsNullOrEmpty(e.key)) continue;
                if (!_lookup.ContainsKey(e.key)) _lookup[e.key] = e.sprite;
                else Debug.LogWarning($"[Missions] Duplicate photo key '{e.key}' — keeping the first.", this);
            }
        }

        public override void Request(string key, Action<Sprite> onLoaded)
        {
            // Rebuilt lazily rather than in OnEnable so edits made while playing take effect.
            if (_lookup == null) BuildLookup();

            Sprite sprite = null;
            if (!string.IsNullOrEmpty(key) && !_lookup.TryGetValue(key, out sprite))
            {
                Debug.LogWarning($"[Missions] No photo for key '{key}'.", this);
                sprite = null;
            }
            onLoaded?.Invoke(sprite != null ? sprite : FallbackSprite);
        }

        /// <summary>Call after editing Photos at runtime.</summary>
        public void Invalidate() => _lookup = null;
    }
}
