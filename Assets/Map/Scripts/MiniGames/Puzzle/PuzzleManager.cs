using Mapbox.Missions;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

public class PuzzleManager : MonoBehaviour
{
    [SerializeField]
    private GameObject piecePrefab;

    [SerializeField]
    private GameObject slotPrefab;

    [Header("UI References")]

    [SerializeField]
    private GameObject puzzlePanel;

    [SerializeField]
    private Transform contentPanel;

    [SerializeField]
    private Image baseImage;

    [SerializeField]
    private GameObject gameStatePanel;

    [SerializeField]
    private GameObject resultStatePanel;

    private bool isGameEnded = false;
    private List<PuzzlePiece> useablePieces = new List<PuzzlePiece>();
    private Dictionary<string, Sprite> puzzleSpriteCache = new Dictionary<string, Sprite>();
    private AsyncOperationHandle<IList<Sprite>> loadHandle;
    private AsyncOperationHandle<Sprite> backgroundHandle;

    public static PuzzleManager Instance { get; private set; }

    private void Awake()
    {
        // Enforce a single instance in the scene
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    internal void Begin(PuzzleMissionDetail puzzle)
    {
        LoadBackground(puzzle);

        ClearExistingPieces();
        LoadPieces(puzzle);

        gameStatePanel.SetActive(true);
        resultStatePanel.SetActive(false);
        puzzlePanel.SetActive(true);
    }

    private void LoadBackground(PuzzleMissionDetail puzzle)
    {
        UnloadPreviousBackground();
        baseImage.enabled = false;
        backgroundHandle = Addressables.LoadAssetAsync<Sprite>(puzzle.BaseImageKey);

        backgroundHandle.Completed += (handle) =>
        {
            baseImage.sprite = handle.Result;
            baseImage.enabled = handle.Result != null;
        };
    }

    private void ClearExistingPieces()
    {
        isGameEnded = false;
        useablePieces.Clear();

        foreach (Transform child in contentPanel)
        {
            Destroy(child.gameObject);
        }
        foreach (Transform child in baseImage.GetComponent<Transform>())
        {
            Destroy(child.gameObject);
        }
    }

    private void LoadPieces(PuzzleMissionDetail puzzle)
    {
        UnloadPreviousPuzzle();

        List<PuzzlePieceData> pieces = puzzle.Pieces;

        List<string> imageKeys = new List<string>();
        foreach (PuzzlePieceData piece in pieces)
        {
            imageKeys.Add(piece.ImageKey);
        }

        // Load images from addressable
        loadHandle = Addressables.LoadAssetsAsync<Sprite>(imageKeys, (sprite) =>
        {
            if (!puzzleSpriteCache.ContainsKey(sprite.name))
            {
                puzzleSpriteCache.Add(sprite.name, sprite);
            }
        }, Addressables.MergeMode.Union);

        loadHandle.Completed += (handle) =>
        {
            // Create pieces game object
            foreach (PuzzlePieceData piece in pieces)
            {
                GameObject slotObject = Instantiate(slotPrefab, contentPanel, false);
                GameObject pieceObject = Instantiate(piecePrefab, slotObject.GetComponent<Transform>(), false);
                PuzzlePiece puzzlePiece = pieceObject.GetComponent<PuzzlePiece>();
                puzzlePiece.pieceId = piece.PieceId;
                Sprite imageSprite;
                puzzleSpriteCache.TryGetValue(piece.ImageKey, out imageSprite);
                puzzlePiece.imageSprite = imageSprite;
                puzzlePiece.Initialise(new Vector2(piece.CorrectX, piece.CorrectY), baseImage.GetComponent<RectTransform>());

                if (piece.CorrectX >= 0 && piece.CorrectY >= 0)
                {
                    useablePieces.Add(puzzlePiece);
                }
            }
        };
    }

    public void UnloadPreviousPuzzle()
    {
        // Check if the handle is valid before releasing
        if (loadHandle.IsValid())
        {
            // This frees the textures from Android's RAM and clears the reference count
            Addressables.Release(loadHandle);
        }

        // Clear your local dictionary so old keys are wiped out
        puzzleSpriteCache.Clear();
        Debug.Log("All previous puzzle pieces unloaded!");
    }

    public void UnloadPreviousBackground()
    {
        // Release previous background to prevent memory leaks
        if (backgroundHandle.IsValid())
        {
            Addressables.Release(backgroundHandle);
        }
        Debug.Log("Previous background unloaded!");
    }
    internal void CheckEndGame()
    {
        if (isGameEnded) return;

        foreach (PuzzlePiece piece in useablePieces)
        {
            if (!piece.isPlaced) return;
        }

        isGameEnded = true;
        StartCoroutine(EndGameRoutine());
    }

    private IEnumerator EndGameRoutine()
    {
        yield return new WaitForSeconds(0.5f);

        gameStatePanel.SetActive(false);
        resultStatePanel.SetActive(true);
    }
}
