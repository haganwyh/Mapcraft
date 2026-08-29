using System.Collections.Generic;
using UnityEngine;

namespace Mapbox.Missions.Puzzles
{
    /// <summary>
    /// Demonstrates the decode path for a two-CSV puzzle setup:
    /// 1. Base Images (mission_id -> base_image_id)
    /// 2. Pieces (piece_id, image_id, correct_x, correct_y -> assigned to mission_id)
    /// </summary>
    public static class PuzzleCsvReader
    {
        private static readonly string[] BaseRequiredColumns =
            { "mission_id", "base_image_key" };

        private static readonly string[] PieceRequiredColumns =
            { "mission_id", "piece_id", "image_key", "correct_x", "correct_y" };

        /// <param name="baseCsvText">Raw file contents for base images.</param>
        /// <param name="piecesCsvText">Raw file contents for the puzzle pieces.</param>
        /// <param name="validMissionIds">Optional validation set.</param>
        public static Dictionary<string, PuzzleMissionDetail> Load(
            string baseCsvText,
            string piecesCsvText,
            HashSet<string> validMissionIds = null)
        {
            var result = new Dictionary<string, PuzzleMissionDetail>();

            // ==========================================
            // 1. PARSE BASE IMAGES
            // ==========================================
            var baseRows = CsvUtil.ParseCsv(baseCsvText);
            if (baseRows.Count > 0)
            {
                var col = CsvUtil.BuildColumnIndex(baseRows[0]);
                if (!CheckRequiredColumns(col, BaseRequiredColumns, "Base Images")) return result;

                for (int r = 1; r < baseRows.Count; r++)
                {
                    var row = baseRows[r];
                    if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0])) continue; // blank line

                    string Get(string column) =>
                        col.TryGetValue(column, out int i) && i < row.Count ? row[i].Trim() : "";

                    string missionId = Get("mission_id");
                    string baseImageKey = Get("base_image_key");

                    if (string.IsNullOrEmpty(missionId))
                    {
                        Debug.LogWarning($"[PuzzleCsv] Base Images Row {r + 1}: missing MissionId — skipped.");
                        continue;
                    }

                    if (validMissionIds != null && !validMissionIds.Contains(missionId))
                        Debug.LogWarning($"[PuzzleCsv] Base Images Row {r + 1}: MissionId '{missionId}' has no match in missions.json.");

                    if (!result.ContainsKey(missionId))
                    {
                        result[missionId] = new PuzzleMissionDetail
                        {
                            MissionId = missionId,
                            BaseImageKey = baseImageKey
                        };
                    }
                }
            }

            // ==========================================
            // 2. PARSE PIECES
            // ==========================================
            var pieceRows = CsvUtil.ParseCsv(piecesCsvText);
            if (pieceRows.Count > 0)
            {
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

                    // Ensure the piece belongs to a mission established in the Base CSV
                    if (!result.TryGetValue(missionId, out var missionData))
                    {
                        Debug.LogWarning($"[PuzzleCsv] Pieces Row {r + 1}: MissionId '{missionId}' not found in Base CSV. Skipped piece '{pieceId}'.");
                        continue;
                    }

                    float.TryParse(Get("correct_x"), out float cX);
                    float.TryParse(Get("correct_y"), out float cY);

                    missionData.Pieces.Add(new PuzzlePieceData
                    {
                        PieceId = pieceId,
                        ImageId = Get("image_key"),
                        CorrectX = cX,
                        CorrectY = cY
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Helper to validate columns before attempting to parse rows.
        /// </summary>
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