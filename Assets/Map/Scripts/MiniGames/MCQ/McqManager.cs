using Mapbox.Missions;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class McqManager : MonoBehaviour
{

    [Header("UI References")]
    [SerializeField]
    private GameObject optionButtonPrefab;

    [SerializeField]
    private GameObject mcqPanel;

    [SerializeField]
    private GameObject gameStatePanel;

    [SerializeField]
    private GameObject resultStatePanel;

    [SerializeField]
    private Transform optionsPanel;

    [SerializeField]
    private TextMeshProUGUI titleText;

    [SerializeField]
    private TextMeshProUGUI remainText;

    [SerializeField]
    private TextMeshProUGUI questionText;

    [SerializeField]
    private TextMeshProUGUI scoreText;

    private List<McqQuestion> questions;
    private int currentIndex;
    private int maxQuestion;
    private int score;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        McqOptionButtons.McqOptionSelected += OptionSelected;
    }

    private void OnDestroy()
    {
        McqOptionButtons.McqOptionSelected -= OptionSelected;
    }

    internal void Begin(McqMissionDetail mcq)
    {
        score = 0;

        titleText.SetText(mcq.MissionTitle);
        questions = mcq.Questions;
        currentIndex = 0;
        maxQuestion = questions.Count;
        ShowCurrentQuestion();

        gameStatePanel.SetActive(true);
        resultStatePanel.SetActive(false);
        mcqPanel.SetActive(true);
    }

    private void ShowCurrentQuestion()
    {
        // Set question text
        questionText.SetText(questions[currentIndex].QuestionText);
        // Set options
        ClearExistingOptions();
        for (int i = 0; i < questions[currentIndex].Options.Count; i++)
        {
            GameObject option = Instantiate(optionButtonPrefab, optionsPanel, false);
            McqOptionButtons mcqOptionButtons = option.GetComponent<McqOptionButtons>();
            mcqOptionButtons.buttonIndex = i;
            option.GetComponentInChildren<TextMeshProUGUI>().SetText(questions[currentIndex].Options[i]);
        }
        // Set remain question text
        remainText.SetText((currentIndex + 1) + " / " + maxQuestion);
    }

    private void ClearExistingOptions()
    {
        foreach (Transform child in optionsPanel)
        {
            Destroy(child.gameObject);
        }
    }

    public void OptionSelected(int buttonIndex)
    {
        if (buttonIndex == questions[currentIndex].CorrectIndex)
        {
            score++;
        }

        // Next question
        currentIndex++;

        if (currentIndex >= maxQuestion)
        {
            // Finished all questions
            scoreText.SetText("Score: " + score);

            gameStatePanel.SetActive(false);
            resultStatePanel.SetActive(true);
        }
        else
        {
            ShowCurrentQuestion();
        }
    }
}
