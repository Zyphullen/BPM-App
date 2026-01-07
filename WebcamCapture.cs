// WebcamCapture.cs — GOD-TIER MINIMAL 2025 EDITION
using UnityEngine;
using UnityEngine.UI;
using System.Collections;

[RequireComponent(typeof(RawImage), typeof(AspectRatioFitter))]
public class WebcamCapture : MonoBehaviour
{
    [Header("Preview (optional)")]
    public RawImage zoomPreview;           // 400×400 UI preview
    public RenderTexture zoomRT { get; private set; }  // For your ML tomorrow
    public RawImage WebcamView;


    [Tooltip("2.2–2.5 = forehead-perfect on every phone")]
    [Range(2f, 3f)] public float zoom = 2.35f;

    WebCamTexture cam;
    RawImage mainView;
    AspectRatioFitter fitter;

    const int TARGET_W = 1280;
    const int TARGET_H = 720;
    const int TARGET_FPS = 30;             // 60 fps = flicker on most phones

    public void StartCam()
    {
        StartCoroutine(StartShowingCam());
    }

    public IEnumerator StartShowingCam()
    {
        mainView = WebcamView;
        fitter = GetComponent<AspectRatioFitter>();

        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam)) yield break;

        // Prefer front-facing, fallback to first
        var device = WebCamTexture.devices[0];
        foreach (var d in WebCamTexture.devices) if (d.isFrontFacing) { device = d; break; } // for mobile

        cam = new WebCamTexture(device.name, TARGET_W, TARGET_H, TARGET_FPS);
        mainView.texture = cam;
        cam.Play();

        // Wait for real size
        while (cam.width < 100) yield return null;

        fitter.aspectRatio = (float)cam.width / cam.height;

        // Power-of-two RT = zero flicker + fastest blit
        zoomRT = new RenderTexture(512, 512, 0, RenderTextureFormat.ARGB32)
        {
            name = "FaceZoomRT",
            useDynamicScale = true
        };
        if (zoomPreview) zoomPreview.texture = zoomRT;

        Debug.Log($"Webcam locked → {cam.width}x{cam.height}@{cam.requestedFPS}fps (front: {device.isFrontFacing})");
    }

    void Update()
    {
        if (!cam || !cam.isPlaying) return;

        // One-liner magic: center-crop zoom with perfect UV math
        Graphics.Blit(cam, zoomRT,
            new Vector2(1f / zoom, 1f / zoom),           // scale
            new Vector2(0.5f - 0.5f / zoom, 0.5f - 0.5f / zoom)); // offset
    }

    // Expose clean face zone for pixel reading / ML
    public Texture FaceTexture => zoomRT;

    void OnDestroy()
    {
        if (cam) { cam.Stop(); cam = null; }
        if (zoomRT) { Destroy(zoomRT); zoomRT = null; }
    }
}