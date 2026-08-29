using Mapbox.Missions;
using Mapbox.Missions.Puzzles;
using Mapbox.Missions.Quiz;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MissionSystemManager : MonoBehaviour
{
    [SerializeField]
    private TextAsset mcqQuestionsCsv;

    [SerializeField]
    private TextAsset puzzleBaseCsv;

    [SerializeField]
    private TextAsset puzzlePiecesCsv;

    [SerializeField]
    private MissionPointManager missionPointManager;

    [SerializeField]
    private McqManager mcqManager;

    [SerializeField]
    private PuzzleManager puzzleManager;

    [SerializeField]
    private TextMeshProUGUI titleText;

    [SerializeField]
    private TextMeshProUGUI descriptionText;

    [SerializeField]
    private Image missionImage;

    private MissionPoint currentMission;
    private Dictionary<string, List<McqQuestion>> mcqQuestions;
    private Dictionary<string, PuzzleMissionDetail> puzzleMissionDetails;

    // Guards against a stale response landing after a NEWER mission has already triggered.
    // Harmless today (MissionPhotoLibrary answers synchronously, same frame) — but load-
    // bearing the moment PhotoProvider is swapped for a real network downloader, since the
    // callback could then fire seconds later, after the player has moved on to another mission.
    private string pendingMissionId;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        missionPointManager.MissionTriggered += OnMissionTriggered;

        mcqQuestions = McqCsvReader.Load(mcqQuestionsCsv.ToString());
        puzzleMissionDetails = PuzzleCsvReader.Load(puzzleBaseCsv.ToString(), puzzlePiecesCsv.ToString(), null);
    }

    private void OnDestroy()
    {
        missionPointManager.MissionTriggered -= OnMissionTriggered;
    }

    private void OnMissionTriggered(MissionPoint missionPoint)
    {
        currentMission = missionPoint;

        // Set UI components
        titleText.text = missionPoint.title;
        descriptionText.text = missionPoint.description;

        pendingMissionId = missionPoint.id;
        string requestedId = missionPoint.id;

        missionPointManager.PhotoProvider.Request(missionPoint.photoKey, sprite =>
        {
            if (pendingMissionId != requestedId) return;   // superseded — ignore

            missionImage.sprite = sprite;
            missionImage.enabled = sprite != null;
        });
    }

    public void OnMissionStart()
    {
        MissionDetail missionDetail = DecodeMissionDetail();
        switch (missionDetail)
        {
            case McqMissionDetail mcq:
                mcqManager.Begin(mcq);
                break;
            case PuzzleMissionDetail puzzle:
                puzzleManager.Begin(puzzle);
                break;
            default:
                // Catches EVERY failure mode in one place: null (DecodeMissionDetail failed)
                // and any future minigame type nobody's wired a case for yet.
                Debug.LogError(missionDetail == null
                    ? "[Missions] MissionStarted fired with a null detail — decode failed."
                    : $"[Missions] No manager wired for minigame type '{missionDetail.GetType().Name}'.");
                break;
        }
    }

    private MissionDetail DecodeMissionDetail()
    {
        if (currentMission.minigameType == MinigameType.Mcq)
        {
            mcqQuestions.TryGetValue(currentMission.id, out List<McqQuestion> currentQuestions);
            var detail = new McqMissionDetail
            {
                MissionId = currentMission.id,
                MissionTitle = currentMission.title,
                MissionDescription = currentMission.description,
                Questions = currentQuestions
                //questions = { new McqQuestion { MissionId = "uk_01", QuestionId = "0", QuestionText = "Test question?", Options = { "A Test", "B Test", "C Test", "D Test" }, CorrectIndex = 0, RewardType = "coin", RewardAmount = 1 } }
            };
            return detail;
        }
        else if (currentMission.minigameType == MinigameType.Puzzle)
        {
            puzzleMissionDetails.TryGetValue(currentMission.id, out PuzzleMissionDetail currentPuzzleDetails);
            currentPuzzleDetails.MissionTitle = currentMission.title;
            currentPuzzleDetails.MissionDescription = currentMission.description;
            return currentPuzzleDetails;
        }
        return null;
    }
}