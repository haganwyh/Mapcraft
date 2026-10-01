using System.Collections.Generic;
using UnityEngine;

namespace Mapbox.Missions.Puzzles
{
    /// <summary>
    /// Decodes the pieces CSV (mission_id, piece_id, image_key, correct_x, correct_y).
    /// Base image keys are detected automatically elsewhere.
    /// </summary>
    public static class PuzzleCsvReader
    {
        private static readonly string[] PieceRequiredColumns =
            { "mission_id", "piece_id", "correct_x", "correct_y" };

        /// <param name="piecesCsvText">Raw file contents for the puzzle pieces.</param>
        /// <param name="validMissionIds">Optional validation set.</param>
        public static Dictionary<string, PuzzleMissionDetail> Load(
            string piecesCsvText,
            HashSet<string> validMissionIds = null)
        {
            var result = new Dictionary<string, PuzzleMissionDetail>();

            var pieceRows = CsvUtil.ParseCsv(piecesCsvText);
            if (pieceRows.Count == 0) return result;

            var col = CsvUtil.BuildColumnIndex(pieceRows[0]);
            if (!CheckRequiredColumns(col, PieceRequiredColumns, "Pieces")) return result;

            for (int r = 1; r < pieceRows.Count; r++)
            {
                var row = pieceRows[r];
                if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0])) continue; // blank line

                string Get(string column) =>
                    col.TryGetValue(column, out int i) && i < row.Count ? row[i].Trim() : "";

                string missionId = Get("mission_id");
                string pieceId = Get("piece_id");

                if (string.IsNullOrEmpty(missionId) || string.IsNullOrEmpty(pieceId))
                {
                    Debug.LogWarning($"[PuzzleCsv] Pieces Row {r + 1}: missing MissionId or PieceId — skipped.");
                    continue;
                }

                // Create the mission entry on first sight (warns once per mission, not per row)
                if (!result.TryGetValue(missionId, out var missionData))
                {
                    if (validMissionIds != null && !validMissionIds.Contains(missionId))
                        Debug.LogWarning($"[PuzzleCsv] Pieces Row {r + 1}: MissionId '{missionId}' has no match in missions.json.");

                    missionData = new PuzzleMissionDetail { MissionId = missionId };
                    result[missionId] = missionData;
                }

                float.TryParse(Get("correct_x"), out float cX);
                float.TryParse(Get("correct_y"), out float cY);

                missionData.Pieces.Add(new PuzzlePieceData
                {
                    PieceId = pieceId,
                    ImageKey = missionId + "_p" + pieceId,
                    CorrectX = cX,
                    CorrectY = cY
                });
            }

            return result;
        }

        private static bool CheckRequiredColumns(Dictionary<string, int> colMap, string[] required, string fileLabel)
        {
            foreach (var req in required)
            {
                if (!colMap.ContainsKey(req))
                {
                    Debug.LogError($"[PuzzleCsv] {fileLabel} missing required column '{req}'. Aborting load.");
                    return false;
                }
            }
            return true;
        }
    }
}