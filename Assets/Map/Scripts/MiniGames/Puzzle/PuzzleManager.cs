using Mapbox.Missions;
using System;
using UnityEngine;
using UnityEngine.UI;

public class PuzzleManager : MonoBehaviour
{

    public MissionPhotoProvider PhotoProvider;

    [Header("UI References")]

    [SerializeField]
    private GameObject puzzlePanel;

    [SerializeField]
    private Image baseImage;

    internal void Begin(PuzzleMissionDetail puzzle)
    {
        PhotoProvider.Request(puzzle.BaseImageKey, sprite =>
        {
            baseImage.sprite = sprite;
            baseImage.enabled = sprite != null;
        });

        puzzlePanel.SetActive(true);
    }
}
