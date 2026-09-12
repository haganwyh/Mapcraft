using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class PuzzlePiece : MonoBehaviour,
    IPointerDownHandler, IPointerUpHandler, IDragHandler, IBeginDragHandler, IEndDragHandler,
    IInitializePotentialDragHandler
{
    [Header("Puzzle Metadata")]
    [NonSerialized] public string pieceId;
    [NonSerialized] public Sprite imageSprite;

    private bool useable;

    [Header("Hold & Drop Settings")]
    [SerializeField] private float holdDuration = 0.5f;
    [SerializeField] private float snapTolerance = 50f;
    [SerializeField] private float scrollCancelThreshold = 50f;
    [SerializeField] private ScrollRect scrollRect;

    // Manager References
    private PuzzleManager puzzleManager;

    // UI References
    private Image pieceImage;
    private RectTransform rectTransform;
    private RectTransform boardParent;
    private Canvas parentCanvas;
    private Camera uiCamera;

    // Slot Tracking
    private Transform slotParent;
    private Vector2 originalSlotSize;

    // Drag State Tracking
    private Vector2 correctTargetPos;
    private Vector2 pointerDownScreenPos;
    private Coroutine holdTimerCoroutine;
    private bool isDragging = false;
    public bool isPlaced = false;

    private void Awake()
    {
        rectTransform = GetComponent<RectTransform>();
        pieceImage = GetComponent<Image>();
        originalSlotSize = rectTransform.sizeDelta; // Dynamically cache initial prefab size

        if (scrollRect == null)
        {
            scrollRect = GetComponentInParent<ScrollRect>();
        }
    }

    private void Start()
    {
        puzzleManager = PuzzleManager.Instance;
    }

    public void Initialise(Vector2 targetExcelPos, RectTransform mainBoardParent)
    {
        correctTargetPos = ConvertDesignToUnityPosition(targetExcelPos);
        boardParent = mainBoardParent;
        slotParent = transform.parent;

        useable = (targetExcelPos.x >= 0 && targetExcelPos.y >= 0); // (-1, -1) = Not a useful piece

        if (scrollRect == null)
        {
            scrollRect = GetComponentInParent<ScrollRect>();
        }

        parentCanvas = boardParent.GetComponentInParent<Canvas>();
        if (parentCanvas != null && parentCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
        {
            uiCamera = parentCanvas.worldCamera != null ? parentCanvas.worldCamera : Camera.main;
        }

        if (imageSprite != null)
        {
            pieceImage.sprite = imageSprite;
        }
        else
        {
            Debug.LogWarning($"PuzzlePiece ({gameObject.name}): Image sprite is missing!");
        }
    }

    private static Vector2 ConvertDesignToUnityPosition(Vector2 designPos)
    {
        float unityX = designPos.x - 540f;  // 1080 / 2
        float unityY = 960f - designPos.y;  // 1920 / 2
        return new Vector2(unityX, unityY);
    }

    #region Event System Handlers

    public void OnInitializePotentialDrag(PointerEventData eventData)
    {
        if (isPlaced || scrollRect == null) return;
        ExecuteEvents.Execute(scrollRect.gameObject, eventData, ExecuteEvents.initializePotentialDrag);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (isPlaced) return;

        pointerDownScreenPos = eventData.position;

        if (holdTimerCoroutine != null) StopCoroutine(holdTimerCoroutine);
        holdTimerCoroutine = StartCoroutine(HoldTimerRoutine(eventData));
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (isPlaced) return;

        if (holdTimerCoroutine != null)
        {
            StopCoroutine(holdTimerCoroutine);
            holdTimerCoroutine = null;
        }

        if (isDragging)
        {
            UpdatePositionToPointer(eventData.position);
            if (!CheckDropPosition()) ReturnToSlot();
        }

        isDragging = false;
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (isPlaced || scrollRect == null) return;

        if (!isDragging)
        {
            ExecuteEvents.Execute(scrollRect.gameObject, eventData, ExecuteEvents.beginDragHandler);
        }
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (isPlaced) return;

        if (isDragging)
        {
            UpdatePositionToPointer(eventData.position);
            CheckDropPosition();
        }
        else if (scrollRect != null)
        {
            if (holdTimerCoroutine != null &&
                Vector2.Distance(eventData.position, pointerDownScreenPos) > scrollCancelThreshold)
            {
                StopCoroutine(holdTimerCoroutine);
                holdTimerCoroutine = null;
            }

            ExecuteEvents.Execute(scrollRect.gameObject, eventData, ExecuteEvents.dragHandler);
        }
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (isPlaced || scrollRect == null) return;

        if (!isDragging)
        {
            ExecuteEvents.Execute(scrollRect.gameObject, eventData, ExecuteEvents.endDragHandler);
        }
    }

    #endregion

    #region Internal Logic

    private IEnumerator HoldTimerRoutine(PointerEventData eventData)
    {
        yield return new WaitForSeconds(holdDuration);

        bool isStillHovering = RectTransformUtility.RectangleContainsScreenPoint(
            rectTransform, eventData.position, uiCamera);

        if (isStillHovering)
        {
            if (scrollRect != null)
            {
                ExecuteEvents.Execute(scrollRect.gameObject, eventData, ExecuteEvents.endDragHandler);
            }

            isDragging = true;
            holdTimerCoroutine = null;

            transform.SetParent(boardParent, true);

            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);

            pieceImage.preserveAspect = false;
            pieceImage.SetNativeSize();

            UpdatePositionToPointer(eventData.position);
        }
    }

    private void UpdatePositionToPointer(Vector2 screenPosition)
    {
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
            boardParent, screenPosition, uiCamera, out Vector2 localPoint))
        {
            rectTransform.anchoredPosition = localPoint;
        }
    }

    private bool CheckDropPosition()
    {
        float distance = Vector2.Distance(rectTransform.anchoredPosition, correctTargetPos);

        if (distance <= snapTolerance)
        {
            // Drop on correct position!
            rectTransform.anchoredPosition = correctTargetPos;
            isPlaced = true;

            if (slotParent != null)
            {
                Destroy(slotParent.gameObject);
            }

            puzzleManager.CheckEndGame();

            this.enabled = false;
            return true;
        }

        return false;
    }

    public void ReturnToSlot()
    {
        transform.SetParent(slotParent, false);

        rectTransform.anchoredPosition = Vector2.zero;

        pieceImage.preserveAspect = true;
        rectTransform.sizeDelta = originalSlotSize;
    }

    #endregion
}