using Mapbox.Missions;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class PuzzleManager : MonoBehaviour
{

    public MissionPhotoProvider PhotoProvider;

    [SerializeField]
    private GameObject piecePrefab;

    [Header("UI References")]

    [SerializeField]
    private GameObject puzzlePanel;

    [SerializeField]
    private Transform contentPanel;

    [SerializeField]
    private Image baseImage;

    internal void Begin(PuzzleMissionDetail puzzle)
    {
        PhotoProvider.Request(puzzle.BaseImageKey, sprite =>
        {
            baseImage.sprite = sprite;
            baseImage.enabled = sprite != null;
        });

        ClearExistingPieces();
        List<PuzzlePieceData> pieces = puzzle.Pieces;
        foreach (PuzzlePieceData piece in pieces)
        {
            GameObject pieceObject = Instantiate(piecePrefab, contentPanel, false);
            PuzzlePiece puzzlePiece = pieceObject.GetComponent<PuzzlePiece>();
            puzzlePiece.pieceId = piece.PieceId;
            puzzlePiece.imageKey = piece.ImageKey;
            puzzlePiece.Initialise();
        }

        puzzlePanel.SetActive(true);
    }

    private void ClearExistingPieces()
    {
        foreach (Transform child in contentPanel)
        {
            Destroy(child.gameObject);
        }
    }
}
