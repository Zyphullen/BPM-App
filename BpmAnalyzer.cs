using System.Collections.Generic;
using UnityEngine;
using TMPro;
using System.Linq;
using System.Text;

[System.Serializable]
public class PatternInfo
{
    public string patternName;
    public int count;
    public float averageIbi;
    public int estimatedBpm;
    public float variability;
    public List<float> ibis = new List<float>();

    public string description => $"{count}x beats, ±{variability:0.000}s var";
}

public class BpmAnalyzer : MonoBehaviour
{
    [Header("Output")]
    public TextMeshProUGUI bpmText;
    public TextMeshProUGUI debugText;

    [Header("Debug")]
    public List<PatternInfo> detectedPatterns = new List<PatternInfo>();

    [Header("=== SIGNAL GATING ===")]
    [Range(0.05f, 0.15f)]
    public float maxPlausiblePulseValue = 0.09f;

    [Range(0.0001f, 0.002f)]
    public float minPlausiblePulseValue = 0.0005f;

    [Header("=== PEAK DETECTION ===")]
    [Range(0.3f, 0.6f)]
    public float relativeThreshold = 0.42f;

    [Range(0.35f, 0.45f)]
    public float minTimeBetweenBeats = 0.375f;  // 160 BPM max

    [Header("=== PATTERN CLUSTERING ===")]
    [Range(15f, 30f)]
    public float tolerancePercent = 22f;

    [Range(4, 10)]
    public int minBeatsForLock = 6;

    [Range(0.15f, 0.35f)]
    public float maxAllowedVariability = 0.25f;

    // Private state
    private List<float> beatTimestamps = new List<float>();
    private float recentPeak = 0f;
    private float recentBaseline = 0f;
    private float recentTrough = 0f;
    private bool wasAboveThreshold = false;
    private bool waitingForTrough = false;
    private string lastRejection = "";

    public void RegisterPulseSample(float pulseValue, float timestamp)
    {
        // === SIGNAL GATING ===
        if (pulseValue > maxPlausiblePulseValue)
        {
            lastRejection = "TOO LOUD";
            UpdateDebugDisplay(pulseValue, 0, 0, true);
            UpdateBpmDisplay();  // Always show current strongest pattern
            return;
        }
        if (pulseValue < minPlausiblePulseValue)
        {
            lastRejection = "TOO QUIET";
            UpdateDebugDisplay(pulseValue, 0, 0, true);
            UpdateBpmDisplay();
            return;
        }

        // Adaptive midline
        recentBaseline = Mathf.Lerp(recentBaseline, pulseValue, 0.008f);

        // Track peak/trough
        recentPeak = Mathf.Max(recentPeak * 0.99f, pulseValue);
        recentTrough = Mathf.Min(recentTrough == 0f ? pulseValue : recentTrough * 1.01f, pulseValue);

        float range = Mathf.Max(recentPeak - recentTrough, 0.0001f);
        float upperThresh = recentBaseline + range * relativeThreshold;
        float lowerThresh = recentBaseline - range * relativeThreshold * 0.85f;

        bool isAbove = pulseValue > upperThresh;
        bool isBelow = pulseValue < lowerThresh;

        // State machine: require proper up-down-up
        if (!waitingForTrough && isAbove && !wasAboveThreshold)
        {
            waitingForTrough = true;
        }

        if (waitingForTrough && isBelow)
        {
            waitingForTrough = false;

            // Refractory
            if (beatTimestamps.Count > 0)
            {
                float sinceLast = timestamp - beatTimestamps[beatTimestamps.Count - 1];
                if (sinceLast < minTimeBetweenBeats)
                {
                    lastRejection = "REFRACTORY";
                    UpdateDebugDisplay(pulseValue, upperThresh, lowerThresh, true);
                    UpdateBpmDisplay();
                    return;
                }
            }

            beatTimestamps.Add(timestamp);
            ProcessNewInterval();
            lastRejection = "OK";
        }

        wasAboveThreshold = isAbove;
        UpdateDebugDisplay(pulseValue, upperThresh, lowerThresh, false);
        UpdateBpmDisplay();  // Always refresh display
    }

    private void ProcessNewInterval()
    {
        if (beatTimestamps.Count < 2) return;

        float latestIbi = beatTimestamps[beatTimestamps.Count - 1] - beatTimestamps[beatTimestamps.Count - 2];

        // 60-160 BPM clamp
        if (latestIbi < 0.375f || latestIbi > 1.0f) return;

        float tolerance = latestIbi * (tolerancePercent / 100f);
        PatternInfo match = detectedPatterns.FirstOrDefault(p => Mathf.Abs(p.averageIbi - latestIbi) <= tolerance);

        if (match != null)
        {
            match.ibis.Add(latestIbi);
            match.count++;
            match.averageIbi = match.ibis.Average();
            match.estimatedBpm = Mathf.RoundToInt(60f / match.averageIbi);
            if (match.ibis.Count > 1)
                match.variability = match.ibis.Max() - match.ibis.Min();
        }
        else
        {
            int bpm = Mathf.RoundToInt(60f / latestIbi);
            var newP = new PatternInfo
            {
                patternName = $"{bpm} BPM",
                count = 1,
                averageIbi = latestIbi,
                estimatedBpm = bpm,
                variability = 0f
            };
            newP.ibis.Add(latestIbi);
            detectedPatterns.Add(newP);
        }

        // SORT STRICTLY BY COUNT ONLY (highest count = top)
        detectedPatterns = detectedPatterns.OrderByDescending(p => p.count).ToList();
    }

    private void UpdateBpmDisplay()
    {
        if (bpmText == null) return;

        if (detectedPatterns.Count == 0)
        {
            bpmText.text = "-- BPM";
            bpmText.color = Color.gray;
            return;
        }

        // Always use the pattern with highest count
        var top = detectedPatterns[0];
        bool goodVar = top.variability <= maxAllowedVariability;

        if (top.count >= minBeatsForLock && goodVar)
        {
            bpmText.text = $"{top.estimatedBpm} BPM";
            bpmText.color = Color.green;
        }
        else if (top.count >= 4 && goodVar)
        {
            bpmText.text = $"{top.estimatedBpm} BPM";
            bpmText.color = Color.cyan;
        }
        else
        {
            bpmText.text = $"{top.estimatedBpm} BPM ({top.count}x)";
            bpmText.color = Color.yellow;
        }
    }

    private void UpdateDebugDisplay(float pulseValue, float upper, float lower, bool rejected)
    {
        if (debugText == null) return;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Sig: {pulseValue:0.0000} {(rejected ? $"[REJECT: {lastRejection}]" : "OK")}");
        sb.AppendLine($"Mid: {recentBaseline:0.0000} | Up: {upper:0.0000} | Dn: {lower:0.0000}");
        sb.AppendLine($"State: {(waitingForTrough ? "WaitDip" : "Ready")} | Beats: {beatTimestamps.Count}");
        sb.AppendLine();
        sb.AppendLine("=== All PATTERNS ===");

        var patterns = detectedPatterns.Where(p => p.count >= 2).OrderByDescending(p => p.count).ToList();
        if (patterns.Count == 0)
            sb.AppendLine("Building pattern...");
        else
            foreach (var p in patterns)
                sb.AppendLine($"{p.estimatedBpm} BPM → {p.count}x | IBI {p.averageIbi:0.000}s | var ±{p.variability:0.000}s");

        debugText.text = sb.ToString();
    }

    public void Reset()
    {
        beatTimestamps.Clear();
        detectedPatterns.Clear();
        recentPeak = recentBaseline = recentTrough = 0f;
        wasAboveThreshold = waitingForTrough = false;
        lastRejection = "";

        if (bpmText) { bpmText.text = "-- BPM"; bpmText.color = Color.gray; }
        if (debugText) debugText.text = "CLEARED";
    }
}