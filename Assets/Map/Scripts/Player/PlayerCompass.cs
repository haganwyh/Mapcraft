using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Rotates the player to the direction the DEVICE is physically aimed, in any posture —
/// flat on a table, held upright, or in between. Modeled on how the NOAA mobile compass
/// (ngdc.noaa.gov) and the OS compass actually work.
///
/// CONTRACT: world rotation (0,0,0) = facing TRUE NORTH (world +Z). This writes
/// transform.rotation (world space). Position is never touched.
///
/// THE NOAA / BROWSER ARCHITECTURE, AND WHY IT MATTERS
///
/// A web compass has exactly one sensor source: on iOS, webkitCompassHeading (CoreLocation's
/// own heading — the page does no math); on Android, deviceorientationabsolute — the
/// ROTATION VECTOR: the OS's absolute, north-referenced, continuously self-calibrating fused
/// orientation. The page converts that orientation to a heading with fixed geometry and
/// NOTHING ELSE. There is no filter re-anchoring the orientation to a scalar compass value.
///
/// The previous version of this script had such an anchor, continuously trimming toward
/// Input.compass.trueHeading. trueHeading is posture-sensitive; any mismatch between our
/// attitude-derived heading and the OS scalar got absorbed into the anchor in one posture
/// and exposed in the other — correct in the hand, wrong on the table.
///
/// THIS VERSION mirrors the browser's trust hierarchy:
///
///   1. The fused attitude (Input.gyro.attitude — on Android the same rotation-vector
///      sensor the browser reads) carries the heading at ALL times, in ALL postures.
///   2. The OS scalar heading is used ONLY to calibrate a constant reference offset
///      (needed because iOS attitude yaw is arbitrary, and Android's is magnetic-north
///      referenced — the offset also swallows declination, which is what NOAA queries
///      their WMM model for). The offset is set exclusively from samples that BOTH report
///      good accuracy AND agree with each other over a stability window. In a degraded
///      magnetic environment no samples qualify, the offset freezes, and the attitude runs
///      free — which is exactly the browser's behaviour on a table.
///   3. A drift watchdog re-runs the same gated calibration if good, stable samples later
///      disagree persistently (long-session gyro drift, iOS reference reset).
///
/// LIMIT SHARED WITH EVERY COMPASS INCLUDING NOAA'S: if the OS fusion itself is poisoned by
/// a strongly magnetized surface for long enough, no app-level code can recover; only
/// distance and a figure-8 recalibration can.
///
/// SETUP
///  - Put this on the player.
///  - UNCHECK "Face Player In Travel Heading" on PlayerMovement (it also writes rotation).
///  - Assign DebugLabel while calibrating; clear for release.
/// </summary>
public class PlayerCompass : MonoBehaviour
{
    [Header("Calibration")]
    [Tooltip("Escape hatch if the character turns the WRONG WAY (mirrored) on your device.")]
    public bool InvertRotationDirection = false;

    [Tooltip("Constant correction, degrees, added at the very end. Prefer fixing a rotated " +
             "character model in the hierarchy so this keeps meaning 'compass correction'.")]
    public float HeadingOffsetDegrees = 0f;

    [Header("Reference calibration (attitude -> true north)")]
    [Tooltip("Only compass samples at least this accurate (degrees) can calibrate the " +
             "reference offset. 0 = accept any sample the OS marks valid.")]
    public float MaxAcceptableHeadingAccuracy = 20f;

    [Tooltip("The last several offset samples must agree within this many degrees before " +
             "they are trusted. A distorted desk fails this test and is ignored.")]
    public float StabilityToleranceDegrees = 6f;

    [Tooltip("If nothing passes the quality gates within this many seconds of startup, " +
             "fall back to the median available sample. An imperfect reference beats an " +
             "arbitrary one.")]
    public float BootstrapTimeoutSeconds = 6f;

    [Tooltip("Once calibrated: if good, stable samples disagree with the current reference " +
             "by more than this for DriftSeconds, recalibrate (gyro drift, iOS ref reset).")]
    public float DriftThresholdDegrees = 15f;
    public float DriftSeconds = 5f;

    [Header("Filtering")]
    [Tooltip("How fast the visible facing catches up. Higher = snappier. Frame-rate " +
             "independent. The attitude is already smooth, so this can be high.")]
    public float ResponseSharpness = 10f;

    [Tooltip("Ignore changes smaller than this so the character doesn't twitch at rest.")]
    public float DeadZoneDegrees = 1f;

    [Header("Debug readout (optional)")]
    public TMP_Text DebugLabel;
    public bool LogToConsole = false;
    public float DebugRefreshInterval = 0.1f;

    // --- public readouts --------------------------------------------------------------
    /// <summary>Final filtered heading, degrees clockwise from true north.</summary>
    public float Heading => _smoothed;
    /// <summary>Attitude-derived heading before the reference offset.</summary>
    public float AttitudeHeading { get; private set; }
    /// <summary>Calibrated constant pinning the attitude frame to true north.</summary>
    public float ReferenceOffset => _refOffset;
    /// <summary>True once the reference has been calibrated at least once.</summary>
    public bool Calibrated => _calibrated;
    public float OsTrueHeading => Input.compass.trueHeading;
    public float HeadingAccuracy => Input.compass.headingAccuracy;
    public bool UsingFusedAttitude { get; private set; }

    private bool _ready;
    private float _smoothed;
    private float _applied;
    private bool _hasHeading;

    // Reference calibration state.
    private float _refOffset;
    private bool _calibrated;
    private float _timeSinceStart;
    private double _lastCompassTimestamp;
    private float _driftTimer;

    // Ring buffer of recent offset samples (degrees, wrapped) for the stability test.
    private const int SampleCount = 8;
    private readonly float[] _samples = new float[SampleCount];
    private int _sampleHead;
    private int _samplesFilled;

    private float _debugTimer;
    private string _calSource = "none";

    private IEnumerator Start()
    {
        Input.compass.enabled = true;

        UsingFusedAttitude = SystemInfo.supportsGyroscope;
        if (UsingFusedAttitude)
        {
            Input.gyro.enabled = true;
            Input.gyro.updateInterval = 1f / 60f;
        }
        else
        {
            Debug.LogWarning("PlayerCompass: no gyroscope; falling back to the OS scalar " +
                             "heading (reliable only with the device held flat).");
        }

        // trueHeading requires the location service. LocationProviderFactory usually
        // started it already, so only start it if nobody has.
        if (Input.location.status == LocationServiceStatus.Stopped ||
            Input.location.status == LocationServiceStatus.Failed)
        {
            Input.location.Start();
        }

        var wait = 20f;
        while (Input.location.status == LocationServiceStatus.Initializing && wait > 0f)
        {
            wait -= Time.unscaledDeltaTime;
            yield return null;
        }

        if (Input.location.status != LocationServiceStatus.Running)
        {
            Debug.LogWarning("PlayerCompass: location service is not running (status: " +
                           Input.location.status + "). Compass disabled.");
            yield break;
        }

        yield return new WaitForSeconds(0.5f); // let the fusion settle
        _ready = true;
    }

    private void Update()
    {
        if (!_ready) return;
        _timeSinceStart += Time.deltaTime;

        float target;
        if (UsingFusedAttitude)
        {
            var h = ComputeAttitudeHeading();
            if (float.IsNaN(h)) return;
            AttitudeHeading = h;
            UpdateReferenceCalibration();
            target = AttitudeHeading + _refOffset;
        }
        else
        {
            target = Input.compass.trueHeading;
        }

        target = Mathf.Repeat(target, 360f);

        if (!_hasHeading)
        {
            _smoothed = target;
            _applied = target;
            _hasHeading = true;
        }
        else
        {
            // DeltaAngle takes the short way round, so crossing 0/360 doesn't spin the
            // character a full turn.
            var t = 1f - Mathf.Exp(-ResponseSharpness * Time.deltaTime);
            _smoothed = Mathf.Repeat(_smoothed + Mathf.DeltaAngle(_smoothed, target) * t, 360f);
        }

        if (Mathf.Abs(Mathf.DeltaAngle(_applied, _smoothed)) > DeadZoneDegrees)
            _applied = _smoothed;

        // WORLD rotation: honours "rotation (0,0,0) faces true north" even if the player
        // is ever parented under a rotated object.
        transform.rotation = Quaternion.Euler(0f, _applied + HeadingOffsetDegrees, 0f);
    }

    /// <summary>
    /// Heading of the device's pointing direction from the fused attitude, valid in every
    /// posture. Referenced to the attitude's own yaw frame; NaN if degenerate.
    /// </summary>
    private float ComputeAttitudeHeading()
    {
        // Standard conversion of the right-handed sensor quaternion into Unity's
        // left-handed frame. After it, attitude * Vector3.forward is the back-of-device
        // (rear camera) direction; attitude * Vector3.up is portrait screen-up.
        var q = Input.gyro.attitude;
        var attitude = Quaternion.Euler(90f, 0f, 0f) * new Quaternion(q.x, q.y, -q.z, -q.w);

        var back = attitude * Vector3.forward;
        var screenUp = attitude * ScreenUpAxis();
        back.y = 0f;
        screenUp.y = 0f;

        // Sum, not select: whichever projection is degenerate in the current posture
        // contributes nothing, so the result is continuous as the phone tilts between
        // flat (screen-up carries it) and upright (the back carries it). When flat this
        // reduces to the same geometry as the W3C/NOAA browser formula.
        var pointing = back + screenUp;
        if (pointing.sqrMagnitude < 1e-8f) return float.NaN;

        var heading = Mathf.Atan2(pointing.x, pointing.z) * Mathf.Rad2Deg;
        if (InvertRotationDirection) heading = -heading;
        return Mathf.Repeat(heading, 360f);
    }

    /// <summary>
    /// Calibrates the CONSTANT offset between the attitude frame and true north. This is
    /// the only place the OS scalar heading is consulted, and it can only move the offset
    /// when its samples are accurate AND mutually consistent. Everywhere else — including a
    /// magnetically hostile table — the attitude alone carries the heading, exactly like
    /// the browser compass.
    /// </summary>
    private void UpdateReferenceCalibration()
    {
        // Consume each compass sample once; ignore the frozen pre-warmup zero state.
        var ts = Input.compass.timestamp;
        if (ts <= 0d || ts == _lastCompassTimestamp) return;
        _lastCompassTimestamp = ts;

        var os = Input.compass.trueHeading;
        var acc = Input.compass.headingAccuracy;
        var accuracyOk = acc >= 0f &&
                         (MaxAcceptableHeadingAccuracy <= 0f ||
                          acc <= MaxAcceptableHeadingAccuracy);
        if (!accuracyOk && _calibrated) { _driftTimer = 0f; return; }
        if (!accuracyOk && _timeSinceStart < BootstrapTimeoutSeconds) return;

        // Offset sample: what _refOffset WOULD be if this sample were the truth.
        var sample = Mathf.DeltaAngle(AttitudeHeading, os);
        _samples[_sampleHead] = sample;
        _sampleHead = (_sampleHead + 1) % SampleCount;
        if (_samplesFilled < SampleCount) _samplesFilled++;

        if (_samplesFilled < SampleCount) return;

        // Circular mean + spread of the buffer. A stable buffer means the OS heading and
        // our attitude heading agree on their RELATIVE motion — the signature of a clean
        // reading. A desk-distorted or posture-glitching stream fails this.
        float sx = 0f, sy = 0f;
        for (var i = 0; i < SampleCount; i++)
        {
            sx += Mathf.Sin(_samples[i] * Mathf.Deg2Rad);
            sy += Mathf.Cos(_samples[i] * Mathf.Deg2Rad);
        }
        var mean = Mathf.Atan2(sx, sy) * Mathf.Rad2Deg;

        var maxDev = 0f;
        for (var i = 0; i < SampleCount; i++)
            maxDev = Mathf.Max(maxDev, Mathf.Abs(Mathf.DeltaAngle(_samples[i], mean)));

        var stable = maxDev <= StabilityToleranceDegrees;

        if (!_calibrated)
        {
            // First calibration: stable-and-accurate preferred; after the bootstrap
            // timeout, take the mean of whatever we have — an imperfect reference beats
            // an arbitrary one (an uncalibrated compass is off by a random constant).
            if (stable || _timeSinceStart >= BootstrapTimeoutSeconds)
            {
                _refOffset = mean;
                _calibrated = true;
                _calSource = stable ? "stable" : "bootstrap";
            }
            return;
        }

        // Drift watchdog: recalibrate only if good samples STABLY disagree for a while.
        // Requiring persistence + stability is what keeps a transient magnetic disturbance
        // from re-poisoning an already-correct reference.
        if (stable && Mathf.Abs(Mathf.DeltaAngle(mean, _refOffset)) > DriftThresholdDegrees)
        {
            _driftTimer += Time.deltaTime * SampleCount; // approx: timer advances per sample
            if (_driftTimer >= DriftSeconds)
            {
                _refOffset = mean;
                _driftTimer = 0f;
                _calSource = "drift-recal";
            }
        }
        else
        {
            _driftTimer = 0f;
            // Gentle tracking of small agreed changes (declination refinement etc.).
            if (stable)
                _refOffset += Mathf.DeltaAngle(_refOffset, mean) * 0.05f;
        }
    }

    // LateUpdate so the readout appears even while Update returns early (e.g. waiting for
    // the location service) — a blank label during a failure tells you nothing.
    private void LateUpdate()
    {
        if (DebugLabel == null && !LogToConsole) return;

        _debugTimer -= Time.unscaledDeltaTime;
        if (_debugTimer > 0f) return;
        _debugTimer = Mathf.Max(0.02f, DebugRefreshInterval);

        var yaw = transform.eulerAngles.y;
        var text =
            "<mspace=0.55em>" +
            $"heading   {Heading,6:F1}\n" +
            $"attitude  {AttitudeHeading,6:F1}\n" +
            $"refOffset {_refOffset,6:F1}  cal {_calibrated} ({_calSource})\n" +
            $"os true   {OsTrueHeading,6:F1}  d {Mathf.DeltaAngle(Heading, OsTrueHeading),6:F1}\n" +
            $"accuracy  {HeadingAccuracy,6:F1}\n" +
            $"player Y  {yaw,6:F1}\n" +
            $"orient    {Screen.orientation}\n" +
            $"fused {UsingFusedAttitude}  ready {_ready}  loc {Input.location.status}";

        if (DebugLabel != null) DebugLabel.text = text;
        if (LogToConsole) Debug.Log(text.Replace("\n", " | "));
    }

    /// <summary>Force recalibration (e.g. from a UI button, with the phone held well away
    /// from interference). Clears the sample buffer and the calibrated flag.</summary>
    public void RecalibrateNow()
    {
        _calibrated = false;
        _samplesFilled = 0;
        _sampleHead = 0;
        _driftTimer = 0f;
        _timeSinceStart = 0f;
        _calSource = "manual";
    }

    /// <summary>
    /// Device-space axis pointing "up the screen" for the current screen orientation.
    /// Sensor data is in device coordinates and is never rotated by screen orientation.
    /// </summary>
    private static Vector3 ScreenUpAxis()
    {
        switch (Screen.orientation)
        {
            case ScreenOrientation.LandscapeLeft: return Vector3.right;
            case ScreenOrientation.LandscapeRight: return Vector3.left;
            case ScreenOrientation.PortraitUpsideDown: return Vector3.down;
            default: return Vector3.up;
        }
    }
}