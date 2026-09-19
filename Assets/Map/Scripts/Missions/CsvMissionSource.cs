using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Mapbox.Missions
{
    /// <summary>
    /// Reads missions from a CSV file instead of JSON — one row per mission, so a designer
    /// bulk-adds/sorts/filters missions the same way they already work with quizzes.csv and
    /// puzzles.csv, rather than hand-editing JSON braces per mission.
    ///
    /// MissionPoint itself is UNCHANGED. This is purely a second IMissionSource implementation;
    /// JsonMissionSource is left in place, unused unless something still wants it. Assumes
    /// CsvUtil.ParseCsv / CsvUtil.BuildColumnIndex exist with the signatures used below (the
    /// shared tokenizer split out of QuizCsvReader) — adjust the two calls if your actual
    /// CsvUtil differs.
    /// </summary>
    public class CsvMissionSource : IMissionSource
    {
        private static readonly string[] RequiredColumns =
            { "id", "type", "latitude", "longitude", "title" };

        private readonly TextAsset _csv;

        public CsvMissionSource(TextAsset csv) { _csv = csv; }

        public void Load(Action<List<MissionPoint>> onLoaded)
        {
            var result = new List<MissionPoint>();

            if (_csv == null || string.IsNullOrWhiteSpace(_csv.text))
            {
                Debug.LogWarning("[Missions] No mission CSV assigned, or it is empty.");
                onLoaded?.Invoke(result);
                return;
            }

            var rows = CsvUtil.ParseCsv(_csv.text);
            if (rows.Count == 0) { onLoaded?.Invoke(result); return; }

            var col = CsvUtil.BuildColumnIndex(rows[0]);
            foreach (var required in RequiredColumns)
            {
                if (!col.ContainsKey(required))
                {
                    Debug.LogError($"[Missions] missions.csv is missing required column '{required}'. Aborting load.");
                    onLoaded?.Invoke(result);
                    return;
                }
            }

            var seen = new HashSet<string>();

            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0])) continue;   // blank line

                string Get(string column) =>
                    col.TryGetValue(column, out int i) && i < row.Count ? row[i].Trim() : "";

                string id = Get("id");
                if (string.IsNullOrEmpty(id))
                {
                    Debug.LogWarning($"[Missions] Row {r + 1}: missing id — skipped.");
                    continue;
                }
                // Duplicate ids would produce two markers fighting over one identity.
                if (!seen.Add(id))
                {
                    Debug.LogWarning($"[Missions] Row {r + 1}: duplicate id '{id}' — keeping the first.");
                    continue;
                }

                // Same rule JsonMissionSource already enforces, just relocated: resolve the
                // type explicitly and fail LOUD on anything unrecognized, rather than letting a
                // typo silently become a wrong default.
                string typeText = Get("type");
                if (!Enum.TryParse(typeText, ignoreCase: true, out MinigameType parsedType))
                {
                    Debug.LogError($"[Missions] Row {r + 1} ('{id}'): invalid or missing type " +
                                   $"'{typeText}' — expected 'Mcq' or 'Puzzle'. Row skipped.");
                    continue;
                }

                // InvariantCulture is not optional here. Excel on a comma-decimal locale (many
                // European locales) can export "22,2936" for a coordinate and switch the CSV
                // delimiter to ';' at the same time -- if that happens the row's FIELD COUNT
                // itself is already wrong and this fails earlier. Assuming the delimiter stayed
                // ',', parsing must still never let the machine's current culture decide whether
                // '.' or ',' means "decimal point" -- that's exactly the kind of thing that
                // works on every dev machine and breaks the moment a designer opens the file on
                // a different-locale computer.
                string latText = Get("latitude");
                string lonText = Get("longitude");
                if (!double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
                {
                    Debug.LogError($"[Missions] Row {r + 1} ('{id}'): invalid latitude '{latText}' — row skipped.");
                    continue;
                }
                if (!double.TryParse(lonText, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                {
                    Debug.LogError($"[Missions] Row {r + 1} ('{id}'): invalid longitude '{lonText}' — row skipped.");
                    continue;
                }

                float interactRadius = 0f;
                if (col.ContainsKey("interactRadiusMetres"))
                    float.TryParse(Get("interactRadiusMetres"), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out interactRadius);

                // [NonSerialized] on minigameType only blocks UNITY's serializer (Inspector/scene
                // YAML) -- plain C# field assignment, as here, is completely unaffected. This is
                // still the only place allowed to set it.
                var mission = new MissionPoint
                {
                    id = id,
                    type = typeText,
                    minigameType = parsedType,
                    latitude = lat,
                    longitude = lon,
                    title = Get("title"),
                    description = Get("description"),
                    photoKey = id,
                    interactRadiusMetres = interactRadius
                };

                result.Add(mission);
            }

            onLoaded?.Invoke(result);
        }
    }
}
