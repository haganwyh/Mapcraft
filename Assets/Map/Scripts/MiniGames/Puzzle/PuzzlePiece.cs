using Mapbox.Missions;
using System;
using UnityEngine;
using UnityEngine.UI;

public class PuzzlePiece : MonoBehaviour
{
    [NonSerialized]
    public string pieceId;
    [NonSerialized]
    public string imageKey;
    [NonSerialized]
    public Sprite imageSprite;

    private Image pieceImage;

    public void Initialise()
    {
        pieceImage = GetComponent<Image>();
        if (imageSprite != null)
        {
            pieceImage.sprite = imageSprite;
        }
        else
        {
            Debug.LogWarning("Puzzle piece image is missing!");
        }
    }
}
