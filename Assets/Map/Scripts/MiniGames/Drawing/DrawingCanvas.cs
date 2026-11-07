using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Freehand drawing surface for a UI mini game.
///
/// Put this on a RawImage whose RectTransform exactly covers the white paper area.
/// - Strokes are rasterised into a transparent Texture2D, so anything placed UNDER this
///   RawImage (e.g. the egg / sparrow hint image) stays visible, and the eraser really erases.
/// - Drawing can only start inside the RawImage rect, and anything outside it is clipped.
/// - Undo / redo work by keeping the list of strokes and replaying them.
///
/// Hook your UI buttons up to: UseBrush, UseEraser, SetColorByIndex, SetColor, Undo, Redo, Clear.
/// </summary>
[RequireComponent(typeof(RawImage))]
public class DrawingCanvas : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    public enum Tool { Brush, Eraser }

    // ---------------------------------------------------------------- Inspector

    [Header("Texture")]
    [Tooltip("Size in pixels of the longest side of the drawing texture.")]
    [SerializeField] private int maxTextureSide = 1024;

    [Header("Brush (sizes are in UI units, i.e. the same units as the RectTransform)")]
    [SerializeField] private Color brushColor = Color.black;
    [SerializeField] private float brushSize = 6f;
    [SerializeField] private float eraserSize = 24f;

    [Header("Optional colour palette (for SetColorByIndex)")]
    [SerializeField] private Color[] palette = new Color[0];

    [Header("Input")]
    [Tooltip("Ignore pointer movement smaller than this (texture pixels).")]
    [SerializeField] private float minPointDistance = 1.5f;

    // ---------------------------------------------------------------- Public state / events

    public Tool CurrentTool { get; private set; } = Tool.Brush;
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public Texture2D Texture => _tex;

    /// <summary>Fired whenever undo/redo availability may have changed (use it to grey out buttons).</summary>
    public event Action HistoryChanged;

    /// <summary>Fired when the tool or colour changes (use it to update button highlights).</summary>
    public event Action ToolChanged;

    // ---------------------------------------------------------------- Internals

    private class Stroke
    {
        public readonly List<Vector2> points = new List<Vector2>();
        public Color32 color;
        public float radius;     // texture pixels
        public bool erase;
    }

    private RawImage _rawImage;
    private RectTransform _rect;
    private Texture2D _tex;
    private int _texW, _texH;
    private float _pixelsPerUnit;
    private bool _dirty;

    private readonly List<Stroke> _undoStack = new List<Stroke>();
    private readonly List<Stroke> _redoStack = new List<Stroke>();
    private Stroke _current;
    private int _activePointer;

    // ---------------------------------------------------------------- Unity messages

    private void Awake()
    {
        _rawImage = GetComponent<RawImage>();
        _rect = _rawImage.rectTransform;
    }

    private void OnEnable()
    {
        EnsureTexture();
    }

    private void OnDisable()
    {
        // Finish a stroke that was interrupted (e.g. panel closed mid-drag).
        EndStroke();
    }

    private void LateUpdate()
    {
        // Upload to the GPU at most once per frame.
        if (_dirty && _tex != null)
        {
            _tex.Apply(false);
            _dirty = false;
        }
    }

    private void OnDestroy()
    {
        if (_tex != null) Destroy(_tex);
    }

    // ---------------------------------------------------------------- Pointer input

    public void OnPointerDown(PointerEventData e)
    {
        if (e.button != PointerEventData.InputButton.Left) return;
        if (_current != null) return;           // another finger is already drawing
        if (!EnsureTexture()) return;
        if (!ScreenToTexture(e, out Vector2 p)) return;

        e.useDragThreshold = false;   // deliver OnDrag from the first pixel of movement
        _activePointer = e.pointerId;
        BeginStroke(p);
    }

    public void OnDrag(PointerEventData e)
    {
        if (_current == null || e.pointerId != _activePointer) return;
        if (!ScreenToTexture(e, out Vector2 p)) return;
        AddPoint(p);
    }

    public void OnPointerUp(PointerEventData e)
    {
        if (_current != null && e.pointerId == _activePointer)
            EndStroke();
    }

    // ---------------------------------------------------------------- Public API for your buttons

    public void UseBrush()
    {
        CurrentTool = Tool.Brush;
        ToolChanged?.Invoke();
    }

    public void UseEraser()
    {
        CurrentTool = Tool.Eraser;
        ToolChanged?.Invoke();
    }

    /// <summary>Sets the colour and switches back to the brush (like most paint apps).</summary>
    public void SetColor(Color color)
    {
        color.a = 1f;
        brushColor = color;
        UseBrush();
    }

    /// <summary>Button-friendly (UnityEvent can't pass a Color): picks palette[index].</summary>
    public void SetColorByIndex(int index)
    {
        if (palette == null || index < 0 || index >= palette.Length) return;
        SetColor(palette[index]);
    }

    public void SetBrushSize(float size) => brushSize = Mathf.Max(0.5f, size);
    public void SetEraserSize(float size) => eraserSize = Mathf.Max(0.5f, size);

    public void Undo()
    {
        EndStroke();
        if (_undoStack.Count == 0) return;

        Stroke s = _undoStack[_undoStack.Count - 1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(s);

        RedrawAll();
        HistoryChanged?.Invoke();
    }

    public void Redo()
    {
        EndStroke();
        if (_redoStack.Count == 0) return;

        Stroke s = _redoStack[_redoStack.Count - 1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(s);

        // Redo only adds one stroke on top, so no need to replay everything.
        if (EnsureTexture())
        {
            NativeArray<Color32> px = _tex.GetRawTextureData<Color32>();
            RasterizeStroke(px, s);
            _dirty = true;
        }
        HistoryChanged?.Invoke();
    }

    /// <summary>Wipes the drawing and the history. Call this when the mini game is (re)opened.</summary>
    public void Clear()
    {
        EndStroke();
        _undoStack.Clear();
        _redoStack.Clear();
        if (EnsureTexture()) RedrawAll();
        HistoryChanged?.Invoke();
    }

    /// <summary>PNG with transparent background (e.g. to save the drawing into the player's diary).</summary>
    public byte[] ExportPNG()
    {
        if (!EnsureTexture()) return null;
        _tex.Apply(false);
        return _tex.EncodeToPNG();
    }

    // ---------------------------------------------------------------- Stroke handling

    private void BeginStroke(Vector2 p)
    {
        bool erase = CurrentTool == Tool.Eraser;
        float size = erase ? eraserSize : brushSize;

        _current = new Stroke
        {
            color = brushColor,
            radius = Mathf.Max(0.5f, size * 0.5f * _pixelsPerUnit),
            erase = erase
        };
        _current.points.Add(p);

        // Draw a dot immediately so a simple tap leaves a mark.
        NativeArray<Color32> px = _tex.GetRawTextureData<Color32>();
        DrawSegment(px, p, p, _current);
        _dirty = true;
    }

    private void AddPoint(Vector2 p)
    {
        Vector2 last = _current.points[_current.points.Count - 1];
        if ((p - last).sqrMagnitude < minPointDistance * minPointDistance) return;

        _current.points.Add(p);

        NativeArray<Color32> px = _tex.GetRawTextureData<Color32>();
        DrawSegment(px, last, p, _current);
        _dirty = true;
    }

    private void EndStroke()
    {
        if (_current == null) return;

        _undoStack.Add(_current);
        _redoStack.Clear();              // a new stroke invalidates the redo history
        _current = null;
        HistoryChanged?.Invoke();
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus) EndStroke();
    }

    // ---------------------------------------------------------------- Texture / rasterisation

    private bool EnsureTexture()
    {
        if (_tex != null) return true;
        if (_rawImage == null)
        {
            _rawImage = GetComponent<RawImage>();
            _rect = _rawImage.rectTransform;
        }

        Canvas.ForceUpdateCanvases();    // make sure the rect has its final size
        Rect r = _rect.rect;
        if (r.width <= 0f || r.height <= 0f) return false;

        float scale = maxTextureSide / Mathf.Max(r.width, r.height);
        _texW = Mathf.Max(1, Mathf.RoundToInt(r.width * scale));
        _texH = Mathf.Max(1, Mathf.RoundToInt(r.height * scale));
        _pixelsPerUnit = _texW / r.width;

        _tex = new Texture2D(_texW, _texH, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "DrawingCanvasTexture"
        };

        NativeArray<Color32> px = _tex.GetRawTextureData<Color32>();
        ClearBuffer(px);
        _tex.Apply(false);

        _rawImage.texture = _tex;
        _rawImage.color = Color.white;
        return true;
    }

    private bool ScreenToTexture(PointerEventData e, out Vector2 texPos)
    {
        texPos = default;
        // Works for rotated / scaled rects and for Overlay, Camera and World-space canvases.
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _rect, e.position, e.pressEventCamera, out Vector2 local))
            return false;

        Rect r = _rect.rect;
        texPos = new Vector2(
            (local.x - r.xMin) / r.width * _texW,
            (local.y - r.yMin) / r.height * _texH);
        return true;
    }

    private void RedrawAll()
    {
        NativeArray<Color32> px = _tex.GetRawTextureData<Color32>();
        ClearBuffer(px);
        for (int i = 0; i < _undoStack.Count; i++)
            RasterizeStroke(px, _undoStack[i]);
        _dirty = true;
    }

    private static void ClearBuffer(NativeArray<Color32> px)
    {
        Color32 clear = new Color32(0, 0, 0, 0);
        for (int i = 0; i < px.Length; i++) px[i] = clear;
    }

    private void RasterizeStroke(NativeArray<Color32> px, Stroke s)
    {
        if (s.points.Count == 1)
        {
            DrawSegment(px, s.points[0], s.points[0], s);
            return;
        }
        for (int i = 1; i < s.points.Count; i++)
            DrawSegment(px, s.points[i - 1], s.points[i], s);
    }

    /// <summary>
    /// Draws a round-capped, anti-aliased line from a to b (texture pixel coordinates).
    /// Anything outside the texture is clipped, which is what keeps drawing inside the canvas.
    /// </summary>
    private void DrawSegment(NativeArray<Color32> px, Vector2 a, Vector2 b, Stroke s)
    {
        float r = s.radius;

        int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x) - r - 1f));
        int maxX = Mathf.Min(_texW - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x) + r + 1f));
        int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y) - r - 1f));
        int maxY = Mathf.Min(_texH - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y) + r + 1f));

        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        float srcAlphaBase = s.color.a / 255f;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float t = lenSq > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq) : 0f;
                float dist = (p - (a + ab * t)).magnitude;

                float coverage = Mathf.Clamp01(r - dist + 0.5f);   // 1px soft edge
                if (coverage <= 0f) continue;

                int i = y * _texW + x;
                Color32 dst = px[i];

                if (s.erase)
                {
                    dst.a = (byte)(dst.a * (1f - coverage));
                    px[i] = dst;
                }
                else
                {
                    float sa = srcAlphaBase * coverage;
                    float da = dst.a / 255f;
                    float oa = sa + da * (1f - sa);
                    if (oa <= 0f) continue;

                    float k = da * (1f - sa);
                    dst.r = (byte)((s.color.r * sa + dst.r * k) / oa + 0.5f);
                    dst.g = (byte)((s.color.g * sa + dst.g * k) / oa + 0.5f);
                    dst.b = (byte)((s.color.b * sa + dst.b * k) / oa + 0.5f);
                    dst.a = (byte)(oa * 255f + 0.5f);
                    px[i] = dst;
                }
            }
        }
    }
}
