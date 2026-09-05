using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mapbox.Missions
{
    /// <summary>
    /// One mission point. Deliberately plain and serializable so the same type carries data
    /// from a project JSON file today and from a server response later without changes.
    /// </summary>

    public enum MinigameType { Mcq, Puzzle }

    public abstract class MissionDetail
    {
        public string MissionId;
        public string MissionTitle;
        public string MissionDescription;
    }

    public class McqMissionDetail : MissionDetail
    {
        public List<McqQuestion> Questions = new List<McqQuestion>();
    }

    public class McqQuestion
    {
        public string MissionId;
        public string QuestionId;
        public string QuestionText;

        public List<string> Options = new List<string>();

        public int CorrectIndex;       // 0: "A" / 1: "B" / 2: "C" / 3: "D" / ...

        public string RewardType;      // e.g. "coin"
        public int RewardAmount;

        public bool IsCorrect(int index) =>
            index == CorrectIndex;
    }

    public class PuzzleMissionDetail : MissionDetail
    {
        public string BaseImageKey;
        public List<PuzzlePieceData> Pieces = new List<PuzzlePieceData>();
    }

    public class PuzzlePieceData
    {
        public string PieceId;
        public string ImageKey;
        public float CorrectX;
        public float CorrectY;
    }

    [Serializable]
    public class MissionPoint
    {
        [Tooltip("Stable unique id. Used for dedupe, completion tracking and logging — never " +
                 "use list position as identity, it changes whenever the file is edited.")]
        public string id;

        public string type;                                 // raw value from JSON ("Mcq" / "Puzzle")
        [NonSerialized] public MinigameType minigameType;   // resolved by JsonMissionSource, nowhere else

        public double latitude;
        public double longitude;

        public string title;
        public string description;

        [Tooltip("Key into the photo provider. A key (not an index) so reordering the photo " +
                 "list can't silently swap images, and a missing photo names itself in the log.")]
        public string photoKey;

        [Tooltip("Optional per-mission interact radius in metres. 0 = use the manager's default.")]
        public float interactRadiusMetres;

        public override string ToString() =>
            $"{(string.IsNullOrEmpty(title) ? "(untitled)" : title)} [{id}]";
    }

    /// <summary>JSON root. JsonUtility cannot parse a top-level array, hence the wrapper.</summary>
    [Serializable]
    public class MissionCollection
    {
        public List<MissionPoint> missions = new();
    }

    /// <summary>
    /// Where missions come from. Callback-shaped even though the JSON implementation answers
    /// synchronously, so swapping in a server source later is a one-line change on the manager
    /// rather than a restructure of its startup path.
    /// </summary>
    public interface IMissionSource
    {
        void Load(Action<List<MissionPoint>> onLoaded);
    }

    /// <summary>Reads missions from a TextAsset JSON file in the project.</summary>
    public class JsonMissionSource : IMissionSource
    {
        private readonly TextAsset _json;

        public JsonMissionSource(TextAsset json) { _json = json; }

        public void Load(Action<List<MissionPoint>> onLoaded)
        {
            var result = new List<MissionPoint>();

            if (_json == null || string.IsNullOrWhiteSpace(_json.text))
            {
                Debug.LogWarning("[Missions] No JSON asset assigned, or it is empty.");
                onLoaded?.Invoke(result);
                return;
            }

            try
            {
                var parsed = JsonUtility.FromJson<MissionCollection>(_json.text);
                if (parsed?.missions != null)
                {
                    var seen = new HashSet<string>();
                    foreach (var m in parsed.missions)
                    {
                        if (m == null) continue;
                        if (string.IsNullOrEmpty(m.id))
                        {
                            Debug.LogWarning($"[Missions] Skipping a mission with no id ({m.title}).");
                            continue;
                        }
                        // Duplicate ids would produce two markers fighting over one identity.
                        if (!seen.Add(m.id))
                        {
                            Debug.LogWarning($"[Missions] Duplicate mission id '{m.id}' — keeping the first.");
                            continue;
                        }

                        // m.type is read as a plain string (see MissionData.cs) specifically because
                        // JsonUtility cannot bind a JSON string onto an int-backed enum field — it
                        // fails silently and leaves the field at its default (index 0) rather than
                        // erroring, which is indistinguishable from a real value unless checked here.
                        if (!Enum.TryParse(m.type, ignoreCase: true, out MinigameType parsedType))
                        {
                            Debug.LogError($"[Missions] Mission '{m.id}' has invalid or missing type " +
                                           $"'{m.type}' — expected 'Mcq' or 'Puzzle'. Mission skipped.");
                            continue;
                        }
                        m.minigameType = parsedType;

                        result.Add(m);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Missions] Failed to parse mission JSON: {e.Message}");
            }

            onLoaded?.Invoke(result);
        }
    }
}
