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
    public MissionPhotoProvider PhotoProvider;

    private Image pieceImage;

    public void Initialise()
    {
        pieceImage = GetComponent<Image>();
        PhotoProvider.Request(imageKey, sprite =>
        {
            pieceImage.sprite = sprite;
            pieceImage.enabled = sprite != null;
        });
    }
}
