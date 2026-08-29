using System;
using UnityEngine;

namespace Mapbox.Missions
{
    /// <summary>
    /// Resolves a mission's photoKey to a Sprite.
    ///
    /// ASYNC-SHAPED ON PURPOSE. The local library answers immediately, but the callback means
    /// a future MissionPhotoDownloader (UnityWebRequestTexture + disk cache) can replace it
    /// without touching MissionMarker or the manager — the marker already tolerates a photo
    /// arriving a frame or a second after it spawns.
    /// </summary>
    public abstract class MissionPhotoProvider : ScriptableObject
    {
        /// <summary>Requests a photo. onLoaded may be invoked immediately or later; null = not found.</summary>
        public abstract void Request(string key, Action<Sprite> onLoaded);
    }
}
