using UnityEngine;
using UnityEngine.UI;
public class ECGGraphDrawer : MonoBehaviour
{
    [Header("Zoom Control")]
    [Range(1000, 200000)]
    public float sensitivityGain = 50000f;
    public float[] pulseBuffer; private float autoCenterSpeed = 10f;

    private RawImage graphDisplay;
    private RenderTexture graphRT;
    private Texture2D graphTex;
    private const int GRAPH_POINTS = 256;
    private const int FIXED_TEXTURE_HEIGHT = 600;
    private int currentBufferIndex;
    private float baselineOffset = 0f;
    public float currentMin;
    public float currentMax;

    public void Initialize(RawImage display, int points)
    {
        graphDisplay = display;
        graphRT = new RenderTexture(points, FIXED_TEXTURE_HEIGHT, 0);
        graphRT.filterMode = FilterMode.Bilinear;
        graphDisplay.texture = graphRT;
        graphTex = new Texture2D(points, FIXED_TEXTURE_HEIGHT, TextureFormat.RGBA32, false);
        ClearGraph();
        Graphics.Blit(graphTex, graphRT);
    }

    public void ClearGraph()
    {
        Color[] pixels = graphTex.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = Color.black;
        graphTex.SetPixels(pixels);
        graphTex.Apply();
    }

    public void UpdateGraph(int bufferIndex, float[] buffer)
    {
        currentBufferIndex = bufferIndex;
        pulseBuffer = buffer;

        // Auto-center on current signal average
        float currentAverage = 0f;
        for (int i = 0; i < GRAPH_POINTS; i++)
        {
            int idx = (currentBufferIndex + i) % GRAPH_POINTS;
            currentAverage += pulseBuffer[idx];
        }
        currentAverage /= GRAPH_POINTS;
        baselineOffset = Mathf.Lerp(baselineOffset, currentAverage, autoCenterSpeed);

        // Auto-adjust clamping zone
        float targetVisualGap = FIXED_TEXTURE_HEIGHT * 0.4f;
        float dataGap = targetVisualGap / sensitivityGain;
        currentMin = baselineOffset - dataGap * 0.2f;
        currentMax = baselineOffset + dataGap * 0.5f;

        // Clamp outside values to baseline
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] < currentMin || buffer[i] > currentMax)
                buffer[i] = baselineOffset;
        }

        //DrawGlowingECG(0.25f);
        DrawGlowingECG(0.50f);
        Graphics.Blit(graphTex, graphRT);
    }

    private void DrawGlowingECG(float heightSetter)
    {
        ClearGraph();
        float centerY = FIXED_TEXTURE_HEIGHT * heightSetter;

        // Draw waveform
        for (int i = 0; i < GRAPH_POINTS - 1; i++)
        {
            int idx1 = (currentBufferIndex + i) % GRAPH_POINTS;
            int idx2 = (currentBufferIndex + i + 1) % GRAPH_POINTS;
            float val1 = pulseBuffer[idx1];
            float val2 = pulseBuffer[idx2];

            float centered1 = val1 - baselineOffset;
            float centered2 = val2 - baselineOffset;

            float y1 = centerY + (centered1 * sensitivityGain);
            float y2 = centerY + (centered2 * sensitivityGain);
            y1 = Mathf.Clamp(y1, 0, FIXED_TEXTURE_HEIGHT - 1);
            y2 = Mathf.Clamp(y2, 0, FIXED_TEXTURE_HEIGHT - 1);

            DrawThickLine(i, (int)y1, i + 1, (int)y2, Color.red, 0.01f);
        }

        // Draw green limit lines
        float minY = centerY + ((currentMin - baselineOffset) * sensitivityGain);
        float maxY = centerY + ((currentMax - baselineOffset) * sensitivityGain);
        minY = Mathf.Clamp(minY, 0, FIXED_TEXTURE_HEIGHT - 1);
        maxY = Mathf.Clamp(maxY, 0, FIXED_TEXTURE_HEIGHT - 1);
        DrawHorizontalLine((int)minY, Color.green, 2f);
        DrawHorizontalLine((int)maxY, Color.green, 2f);

        graphTex.Apply();
    }

    private void DrawHorizontalLine(int y, Color col, float thick)
    {
        int thickness = Mathf.CeilToInt(thick);
        int half = thickness / 2;
        for (int x = 0; x < GRAPH_POINTS; x++)
        {
            for (int oy = -half; oy <= half; oy++)
            {
                int drawY = y + oy;
                if (drawY >= 0 && drawY < FIXED_TEXTURE_HEIGHT)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int drawX = Mathf.Clamp(x + ox, 0, GRAPH_POINTS - 1);
                        Color existing = graphTex.GetPixel(drawX, drawY);
                        graphTex.SetPixel(drawX, drawY, Color.Lerp(existing, col, col.a));
                    }
                }
            }
        }
    }

    private void DrawThickLine(int x0, int y0, int x1, int y1, Color col, float thick)
    {
        int thickness = Mathf.CeilToInt(thick);
        int half = thickness / 2;
        int dx = Mathf.Abs(x1 - x0);
        int dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int px = x0, py = y0;

        while (true)
        {
            for (int ox = -half; ox <= half; ox++)
            {
                for (int oy = -half; oy <= half; oy++)
                {
                    int drawX = px + ox;
                    int drawY = py + oy;
                    if (drawX >= 0 && drawX < GRAPH_POINTS && drawY >= 0 && drawY < FIXED_TEXTURE_HEIGHT)
                    {
                        Color existing = graphTex.GetPixel(drawX, drawY);
                        graphTex.SetPixel(drawX, drawY, Color.Lerp(existing, col, col.a));
                    }
                }
            }
            if (px == x1 && py == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; px += sx; }
            if (e2 < dx) { err += dx; py += sy; }
        }
    }

    private void OnDestroy()
    {
        if (graphRT) { graphRT.Release(); Destroy(graphRT); }
        if (graphTex) Destroy(graphTex);
    }
}

//dfd/fd//dfdfd
//dfdfdfdfd/

