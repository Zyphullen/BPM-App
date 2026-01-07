using UnityEngine;
using UnityEngine.UI;

public class PulseNoiseVisualizer_WHITE_ON_BLACK : MonoBehaviour
{
    [Header("Displays - MUST assign in Inspector")]
    public RawImage noiseDisplay;
    public RawImage graphDisplay;

    [Header("Tuning Controls")]
    [Range(1, 100)]
    public int motionSensitivity = 50; // diffren on diffrent phones

    [Header("Big Movement Suppression")]
    [Range(5f, 50f)]
    public float maxMovingPixelsPercent = 20f; // 20 seems best // % of pixels allowed to move before suppressing

    [Tooltip("How strongly to fade the noise map when big movement is detected")]
    [Range(0.05f, 0.5f)]
    public float bigMovementFadeStrength = 0.5f; // 0.5f seems best // Higher = faster clear

    [Header("Auto-Tuning")]
    public bool autoTuneNoiseMap = false; // not sure about this yet
    [Header("Auto-Tune Range")]
    public float maxWhitePercentAllowed = 6f;
    public float minWhitePercentAllowed = 4f;

    [Header("White Threshold")]
    [Range(1, 255)]
    public int whiteThreshold = 50;

    [Header("BPM Analysis Control")]
    public bool enableBpmAnalysis = true;

    [Header("Live Feedback")]
    public float currentWhitePercent = 0f;

    private float trailFade = 0.018f;         // Normal slow fade
    private float pulseSmoothing = 0.7f;

    public WebcamCapture cam;
    private RenderTexture noiseRT;

    private Texture2D acc, cur, prev;

    private const int RES = 256;
    private const int GRAPH_POINTS = 256;
    private const int TOTAL_PIXELS = RES * RES;

    public float[] pulseBuffer;
    public int bufferIndex = 0;

    private float smoothedPulse = 0f;

    public ECGGraphDrawer graphDrawer;
    public BpmAnalyzer bpmAnalyzer;

    private float normalizationFactor;

    // Timing
    private float lastTuneTime = 0f;
    private float tuneInterval = 0.2f;
    private const float fastInterval = 0.2f;
    private const float slowInterval = 1.0f;

    private bool slowModeActivated = false;
    private float lastPublicUpdateTime = 0f;
    private const float publicUpdateInterval = 1.0f;

    private bool prevEnableBpmAnalysis = true;

    // Temp storage
    private float totalMotionTemp = 0f;
    private int movingPixelCount = 0;

    public void StartingCam()
    {
        graphDrawer.Initialize(graphDisplay, GRAPH_POINTS);

        noiseRT = new RenderTexture(RES, RES, 0);
        noiseDisplay.texture = noiseRT;

        cur = new Texture2D(RES, RES, TextureFormat.RGB24, false);
        prev = new Texture2D(RES, RES, TextureFormat.RGB24, false);
        acc = new Texture2D(RES, RES, TextureFormat.RGB24, false);
        acc.SetPixels32(new Color32[RES * RES]);
        acc.Apply();

        pulseBuffer = new float[GRAPH_POINTS];
        normalizationFactor = 1f / (RES * RES * 255f);

        prevEnableBpmAnalysis = enableBpmAnalysis;
        currentWhitePercent = 0f;
        slowModeActivated = false;
        tuneInterval = fastInterval;
    }

    void Update()
    {
        if (cam == null || cam.zoomRT == null || graphDrawer == null) return;

        // These are the typical functions called every frame (in Update or a coroutine) 
        // in a photoplethysmography (rPPG) heart-rate-from-camera app.
        // They form a pipeline: read webcam → detect motion → extract green channel signal → calculate BPM → display results.

        ReadCurrentFrameFromZoom();
        // Reads the current pixels from the zoomed/cropped face region (your zoomRT RenderTexture or FaceTexture).
        // Usually done with GetPixels() or AsyncGPUReadback to get the RGB pixel data of the face area.
        // Purpose: Get fresh frame data from the tracked face for signal processing.

        ProcessMotionDetectionAndAccumulate();
        // Analyzes the current frame (or ROI) to detect head motion or bad frames.
        // Often computes frame-to-frame difference, optical flow, or variance in the face region.
        // If too much motion, it flags the frame as invalid and skips or reduces its weight in accumulation.
        // Purpose: Prevent motion artifacts from corrupting the subtle pulse signal.

        SwapFrameBuffers();
        // Swaps between two (or more) frame buffers: e.g., previousFrame ↔ currentFrame.
        // Needed for motion detection (compares current vs previous) and for temporal filtering.
        // Purpose: Keep the previous frame available for next-frame comparisons without extra copying.

        ExtractAndSmoothPulseSignal();
        // Extracts the raw pulse signal, usually the average green channel value across the face ROI (green is most sensitive to blood absorption).
        // Applies bandpass filtering (e.g., 0.7–4 Hz ≈ 42–240 bpm) and smoothing to remove noise while keeping heart-rate frequencies.
        // Often builds a time-series buffer of green mean values.
        // Purpose: Isolate the tiny pulsatile component caused by heartbeat.

        UpdateGraphDisplay();
        // Updates a UI line graph (e.g., UILineRenderer or Texture2D plot) with the latest raw or filtered signal values.
        // Usually scrolls the waveform left and shows real-time pulse wave.
        // Purpose: Visual feedback so the user can see the signal quality and pulse waveform.

        HandleBpmAnalysis();
        // Performs frequency analysis on the accumulated filtered signal buffer.
        // Common methods: FFT (Fast Fourier Transform) or peak detection in time domain to find heart-rate peaks.
        // Finds dominant frequency → converts to BPM (beats per minute).
        // Often averages over a window (e.g., last 8–10 seconds) for stable reading.
        // Purpose: Compute and update the actual heart-rate number.

        PerformAutoTuneIfEnabled();
        // Optional adaptive step: dynamically adjusts skin-tone thresholds, ROI size, bandpass limits, or gain based on current signal quality/SNR.
        // May recalibrate skin color ranges or zoom level if signal is weak.
        // Purpose: Improve tracking robustness under changing lighting or skin tones.

        UpdatePublicWhitePercentFeedback();
        // Calculates and displays how much of the face ROI is "valid" (e.g., percentage of pixels that are skin-colored or not overexposed/white).
        // Often used as a quality indicator: "Keep face in frame, avoid bright light".
        // May show a bar or text like "Skin coverage: 85%".
        // Purpose: Give user real-time feedback to improve signal quality and positioning.
    }

    private void ReadCurrentFrameFromZoom()
    {
        int srcW = cam.zoomRT.width;
        int srcH = cam.zoomRT.height;
        int srcX = Mathf.Clamp((srcW - RES) / 2, 0, srcW - RES);
        int srcY = Mathf.Clamp((srcH - RES) / 2, 0, srcH - RES);

        RenderTexture.active = cam.zoomRT;
        cur.ReadPixels(new Rect(srcX, srcY, RES, RES), 0, 0);
        cur.Apply(false);
    }

    private void ProcessMotionDetectionAndAccumulate()
    {
        Color32[] now = cur.GetPixels32();
        Color32[] old = prev.GetPixels32();
        Color32[] a = acc.GetPixels32();

        float totalMotion = 0f;
        movingPixelCount = 0;

        // First pass: compute motion and count active pixels
        for (int i = 0; i < now.Length; i++)
        {
            int diffG = Mathf.Abs(now[i].g - old[i].g);
            int diffR = Mathf.Abs(now[i].r - old[i].r);
            int diffB = Mathf.Abs(now[i].b - old[i].b);
            int diff = diffG * 4 + diffR + diffB;

            if (diff > motionSensitivity)
            {
                movingPixelCount++;
                byte boost = (byte)Mathf.Clamp(diff * motionSensitivity / 5, 0, 255);
                totalMotion += boost;
            }
        }

        // Detect big movement
        float movingPercent = (float)movingPixelCount / TOTAL_PIXELS * 100f;
        bool isBigMovement = movingPercent > maxMovingPixelsPercent;

        // Apply fading and optional boost
        if (isBigMovement)
        {
            // Strong fade + no new motion added
            for (int i = 0; i < a.Length; i++)
            {
                byte val = (byte)(a[i].r * (1f - bigMovementFadeStrength));
                a[i] = new Color32(val, val, val, 255);
            }
            totalMotion = 0f;  // Ignore this frame for pulse signal
            // Debug.Log($"Big movement suppressed ({movingPercent:F1}% moving)");
        }
        else
        {
            // Normal operation: slow fade + add local motion
            for (int i = 0; i < now.Length; i++)
            {
                byte val = (byte)(a[i].r * (1f - trailFade));

                int diffG = Mathf.Abs(now[i].g - old[i].g);
                int diffR = Mathf.Abs(now[i].r - old[i].r);
                int diffB = Mathf.Abs(now[i].b - old[i].b);
                int diff = diffG * 4 + diffR + diffB;

                if (diff > motionSensitivity)
                {
                    byte boost = (byte)Mathf.Clamp(diff * motionSensitivity / 5, 0, 255);
                    val = (byte)Mathf.Max(val, boost);
                }

                a[i] = new Color32(val, val, val, 255);
            }
        }

        acc.SetPixels32(a);
        acc.Apply(false);
        Graphics.Blit(acc, noiseRT);

        totalMotionTemp = totalMotion;
    }

    private void SwapFrameBuffers()
    {
        (cur, prev) = (prev, cur);
        RenderTexture.active = null;
    }

    private void ExtractAndSmoothPulseSignal()
    {
        float motionNorm = totalMotionTemp * normalizationFactor;
        smoothedPulse = Mathf.Lerp(smoothedPulse, motionNorm, 1f - pulseSmoothing);

        pulseBuffer[bufferIndex] = smoothedPulse;
        bufferIndex = (bufferIndex + 1) % GRAPH_POINTS;
    }

    private void UpdateGraphDisplay()
    {
        graphDrawer.UpdateGraph(bufferIndex, pulseBuffer);
    }

    private void HandleBpmAnalysis()
    {
        if (bpmAnalyzer == null) return;

        if (enableBpmAnalysis != prevEnableBpmAnalysis)
        {
            bpmAnalyzer.Reset();
            prevEnableBpmAnalysis = enableBpmAnalysis;
        }

        if (enableBpmAnalysis)
        {
            bpmAnalyzer.RegisterPulseSample(smoothedPulse, Time.time);
        }
    }

    private void PerformAutoTuneIfEnabled()
    {
        if (!autoTuneNoiseMap || Time.time - lastTuneTime < tuneInterval) return;

        lastTuneTime = Time.time;

        float whitePercent = CalculateCurrentWhitePercent();

        if (whitePercent >= minWhitePercentAllowed && whitePercent <= maxWhitePercentAllowed)
        {
            if (!slowModeActivated)
            {
                slowModeActivated = true;
                tuneInterval = slowInterval;
                Debug.Log($"★ LOCKED & SLOW MODE ★ {whitePercent:F2}% white");
            }
        }
        else
        {
            if (whitePercent > maxWhitePercentAllowed)
                motionSensitivity = Mathf.Min(90, motionSensitivity + 1);
            else if (whitePercent < minWhitePercentAllowed)
                motionSensitivity = Mathf.Max(10, motionSensitivity - 1);
        }
    }

    private void UpdatePublicWhitePercentFeedback()
    {
        if (Time.time - lastPublicUpdateTime < publicUpdateInterval) return;

        lastPublicUpdateTime = Time.time;
        currentWhitePercent = CalculateCurrentWhitePercent();
    }

    private float CalculateCurrentWhitePercent()
    {
        RenderTexture.active = noiseRT;
        acc.ReadPixels(new Rect(0, 0, RES, RES), 0, 0);
        acc.Apply();
        RenderTexture.active = null;

        Color32[] pixels = acc.GetPixels32();
        int whiteCount = 0;
        foreach (Color32 pixel in pixels)
        {
            if (pixel.r > whiteThreshold) whiteCount++;
        }

        return (float)whiteCount / TOTAL_PIXELS * 100f;
    }

    void OnDestroy()
    {
        if (cur) Destroy(cur);
        if (prev) Destroy(prev);
        if (acc) Destroy(acc);
        if (noiseRT) { noiseRT.Release(); Destroy(noiseRT); }
    }
}