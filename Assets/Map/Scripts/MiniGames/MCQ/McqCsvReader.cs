using System.Collections.Generic;
using UnityEngine;

namespace Mapbox.Missions.Quiz
{
    /// <summary>
    /// EXAMPLE / REFERENCE ONLY — demonstrates the decode path discussed in chat:
    /// raw CSV text -> RFC 4180 tokenizer -> header-mapped rows -> QuizQuestion, grouped
    /// and ordered by MissionId. Not yet wired into IQuizSource / the mission system.
    /// </summary>
    public static class McqCsvReader
    {
        private static readonly string[] RequiredColumns =
            { "mission_id", "question_id", "question_text", "option_a", "option_b", "correct_index" };

        /// <param name="csvText">Raw file contents (e.g. a TextAsset's .text).</param>
        /// <param name="validMissionIds">
        /// Optional — pass missions.json's id set to warn on a MissionId typo that has no
        /// matching mission. Null skips that check.
        /// </param>
        public static Dictionary<string, List<McqQuestion>> Load(
            string csvText, HashSet<string> validMissionIds = null)
        {
            var result = new Dictionary<string, List<McqQuestion>>();
            var rows = CsvUtil.ParseCsv(csvText);
            if (rows.Count == 0) return result;

            // Map column NAME to index rather than assuming position, so a designer
            // reordering columns in Excel can't silently scramble which value lands where.
            var col = CsvUtil.BuildColumnIndex(rows[0]);
            foreach (var required in RequiredColumns)
            {
                if (!col.ContainsKey(required))
                {
                    Debug.LogError($"[QuizCsv] Missing required column '{required}'. Aborting load.");
                    return result;
                }
            }

            var seenQuestionIds = new HashSet<string>();   // "missionId::questionId"

            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0])) continue;   // blank line

                string Get(string column) =>
                    col.TryGetValue(column, out int i) && i < row.Count ? row[i].Trim() : "";

                string missionId = Get("mission_id");
                string questionId = Get("question_id");
                if (string.IsNullOrEmpty(missionId) || string.IsNullOrEmpty(questionId))
                {
                    Debug.LogWarning($"[QuizCsv] Row {r + 1}: missing MissionId or QuestionId — skipped.");
                    continue;
                }

                if (validMissionIds != null && !validMissionIds.Contains(missionId))
                    Debug.LogWarning($"[QuizCsv] Row {r + 1}: MissionId '{missionId}' has no match in missions.json.");

                string dupeKey = $"{missionId}::{questionId}";
                if (!seenQuestionIds.Add(dupeKey))
                {
                    Debug.LogWarning($"[QuizCsv] Row {r + 1}: duplicate QuestionId '{questionId}' " +
                                     $"for '{missionId}' — skipped.");
                    continue;
                }

                // Only keep option columns with actual text — this is what lets a 2-option
                // question (blank OptionC/D) and a 4-option question share one file.
                var options = new List<string>();
                foreach (var letter in new[] { "_a", "_b", "_c", "_d", "_e" })
                {
                    string text = Get("option" + letter);
                    if (!string.IsNullOrEmpty(text)) options.Add(text);
                }

                int.TryParse(Get("correct_index"), out int correct);
                if (correct > options.Count)
                {
                    Debug.LogError($"[QuizCsv] Row {r + 1} ('{missionId}'/'{questionId}'): CorrectAnswer " +
                                   $"'{correct}' has no matching non-blank option — question skipped.");
                    continue;
                }

                int.TryParse(Get("reward_amount"), out int rewardAmount);

                var q = new McqQuestion
                {
                    MissionId = missionId,
                    QuestionId = questionId,
                    QuestionText = Get("question_text"),
                    Options = options,
                    CorrectIndex = correct,
                    RewardType = Get("reward_type"),
                    RewardAmount = rewardAmount
                };

                if (!result.TryGetValue(missionId, out var list))
                    result[missionId] = list = new List<McqQuestion>();
                list.Add(q);
            }

            // Keep each mission's questions in QuestionId order (q1, q2, ...) so a quiz
            // plays in the order the rows were written, not dictionary/insertion order.
            foreach (var list in result.Values)
                list.Sort((a, b) => string.CompareOrdinal(a.QuestionId, b.QuestionId));

            return result;
        }
    }
}
