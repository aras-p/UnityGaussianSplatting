// SPDX-License-Identifier: MIT

using System.IO;
using GaussianSplatting.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GaussianSplatting.Editor
{
    [BurstCompile]
    public static class GaussianSplatValidator
    {
        struct RefItem
        {
            public string assetPath;
            public int cameraIndex;
            public float fov;
        }

        // currently on RTX 3080Ti: 43.76, 39.36, 43.50 PSNR
        [MenuItem("Tools/Gaussian Splats/Debug/Validate Render against SBIR")]
        public static void ValidateSBIR()
        {
            ValidateImpl("SBIR");
        }
        // currently on RTX 3080Ti: matches
        [MenuItem("Tools/Gaussian Splats/Debug/Validate Render against D3D12")]
        public static void ValidateD3D12()
        {
            ValidateImpl("D3D12");
        }

        static ValidationRun s_Run;

        static void ValidateImpl(string refPrefix)
        {
            if (s_Run != null)
                return;
            var gaussians = Object.FindObjectOfType(typeof(GaussianSplatRenderer)) as GaussianSplatRenderer;
            if (gaussians == null || !gaussians.isActiveAndEnabled || Camera.main == null)
            {
                Debug.LogError("Validation needs an active GaussianSplatRenderer and a main camera");
                return;
            }
            s_Run = new ValidationRun(gaussians, Camera.main, refPrefix);
            EditorApplication.update += s_Run.Update;
            AssemblyReloadEvents.beforeAssemblyReload += s_Run.BeforeAssemblyReload;
        }

        class ValidationRun
        {
            readonly GaussianSplatRenderer m_Gaussians;
            readonly Camera m_Camera;
            readonly string m_RefPrefix;
            readonly string m_OldSource;
            readonly Vector3 m_OldCamPos;
            readonly Quaternion m_OldCamRot;
            readonly float m_OldCamFov;
            readonly RenderTexture m_OldCamTarget;
            readonly RefItem[] m_Items =
            {
                new() {assetPath = "bicycle", cameraIndex = 0, fov = 39.09651f},
                new() {assetPath = "truck", cameraIndex = 30, fov = 50},
                new() {assetPath = "garden", cameraIndex = 30, fov = 47},
            };
            int m_Index;
            bool m_Loading;

            public ValidationRun(GaussianSplatRenderer gaussians, Camera camera, string refPrefix)
            {
                m_Gaussians = gaussians;
                m_Camera = camera;
                m_RefPrefix = refPrefix;
                m_OldSource = gaussians.m_SourcePath;
                m_OldCamPos = camera.transform.localPosition;
                m_OldCamRot = camera.transform.localRotation;
                m_OldCamFov = camera.fieldOfView;
                m_OldCamTarget = camera.targetTexture;
            }

            public void Update()
            {
                try
                {
                    if (!m_Gaussians || !m_Gaussians.isActiveAndEnabled || !m_Camera)
                    {
                        Finish();
                        return;
                    }
                    if (m_Index >= m_Items.Length)
                    {
                        Finish();
                        return;
                    }
                    var item = m_Items[m_Index];
                    if (!m_Loading)
                    {
                        string path = $"{item.assetPath}.ply";
                        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, path)))
                        {
                            Debug.LogError($"Did not find validation source Assets/StreamingAssets/{path}");
                            ++m_Index;
                            return;
                        }
                        EditorUtility.DisplayProgressBar("Validating Gaussian splat rendering", item.assetPath, (float)m_Index / m_Items.Length);
                        m_Gaussians.m_SourcePath = path;
                        m_Gaussians.Reload();
                        m_Loading = true;
                        return;
                    }
                    if (m_Gaussians.isLoading)
                        return;
                    if (!string.IsNullOrEmpty(m_Gaussians.loadError))
                        Debug.LogError($"Could not load validation item {item.assetPath}: {m_Gaussians.loadError}");
                    else if (!m_Gaussians.HasValidRenderSetup || item.cameraIndex >= m_Gaussians.cameras.Length)
                        Debug.LogError($"Missing splat data or camera {item.cameraIndex} for validation item {item.assetPath}");
                    else
                        ValidateItem(m_Gaussians, m_Camera, item, m_RefPrefix);
                    m_Camera.targetTexture = m_OldCamTarget;
                    ++m_Index;
                    m_Loading = false;
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                    Finish();
                }
            }

            public void Finish()
            {
                Finish(true);
            }

            public void BeforeAssemblyReload()
            {
                Finish(false);
            }

            void Finish(bool reloadSource)
            {
                EditorApplication.update -= Update;
                AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
                if (m_Gaussians)
                {
                    m_Gaussians.m_SourcePath = m_OldSource;
                    if (reloadSource) m_Gaussians.Reload();
                }
                if (m_Camera)
                {
                    m_Camera.targetTexture = m_OldCamTarget;
                    m_Camera.transform.localPosition = m_OldCamPos;
                    m_Camera.transform.localRotation = m_OldCamRot;
                    m_Camera.fieldOfView = m_OldCamFov;
                }
                EditorUtility.ClearProgressBar();
                s_Run = null;
            }
        }

        static unsafe void ValidateItem(GaussianSplatRenderer gaussians, Camera cam, RefItem item, string refPrefix)
        {
            var refImageFile = $"../../docs/RefImages/{refPrefix}_{item.assetPath}{item.cameraIndex}.png";
            if (!File.Exists(refImageFile))
            {
                Debug.LogError($"Did not find reference image for validation item {item.assetPath} at {refImageFile}");
                return;
            }
            var compareTexture = new Texture2D(4, 4, GraphicsFormat.R8G8B8A8_SRGB, TextureCreationFlags.None);
            byte[] refImageBytes = File.ReadAllBytes(refImageFile);
            ImageConversion.LoadImage(compareTexture, refImageBytes, false);

            int width = compareTexture.width;
            int height = compareTexture.height;

            var previousTarget = cam.targetTexture;
            var previousActive = RenderTexture.active;
            var renderTarget = RenderTexture.GetTemporary(width, height, 24, GraphicsFormat.R8G8B8A8_SRGB);
            cam.targetTexture = renderTarget;
            cam.fieldOfView = item.fov;

            var captureTexture = new Texture2D(width, height, GraphicsFormat.R8G8B8A8_SRGB, TextureCreationFlags.None);
            NativeArray<Color32> diffPixels = new(width * height, Allocator.Persistent);

            try
            {
                gaussians.ActivateCamera(item.cameraIndex);
                cam.Render();
                Graphics.SetRenderTarget(renderTarget);
                captureTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);

                NativeArray<Color32> refPixels = compareTexture.GetPixelData<Color32>(0);
                NativeArray<Color32> gotPixels = captureTexture.GetPixelData<Color32>(0);
                float psnr = 0, rmse = 0;
                int errorsCount = 0;
                DiffImagesJob difJob = new DiffImagesJob();
                difJob.difPixels = diffPixels;
                difJob.refPixels = refPixels;
                difJob.gotPixels = gotPixels;
                difJob.psnrPtr = &psnr;
                difJob.rmsePtr = &rmse;
                difJob.difPixCount = &errorsCount;
                difJob.Schedule().Complete();

                string pathDif = $"../../Shot-{refPrefix}-{item.assetPath}{item.cameraIndex}-diff.png";
                string pathRef = $"../../Shot-{refPrefix}-{item.assetPath}{item.cameraIndex}-ref.png";
                string pathGot = $"../../Shot-{refPrefix}-{item.assetPath}{item.cameraIndex}-got.png";

                if (errorsCount > 50 || psnr < 90.0f)
                {
                    Debug.LogWarning(
                        $"{refPrefix} {item.assetPath} cam {item.cameraIndex}: RMSE {rmse:F2} PSNR {psnr:F2} diff pixels {errorsCount:N0}");

                    NativeArray<byte> pngBytes = ImageConversion.EncodeNativeArrayToPNG(diffPixels,
                        GraphicsFormat.R8G8B8A8_SRGB, (uint) width, (uint) height);
                    File.WriteAllBytes(pathDif, pngBytes.ToArray());
                    pngBytes.Dispose();
                    pngBytes = ImageConversion.EncodeNativeArrayToPNG(refPixels, GraphicsFormat.R8G8B8A8_SRGB,
                        (uint) width, (uint) height);
                    File.WriteAllBytes(pathRef, pngBytes.ToArray());
                    pngBytes.Dispose();
                    pngBytes = ImageConversion.EncodeNativeArrayToPNG(gotPixels, GraphicsFormat.R8G8B8A8_SRGB,
                        (uint) width, (uint) height);
                    File.WriteAllBytes(pathGot, pngBytes.ToArray());
                    pngBytes.Dispose();
                }
                else
                {
                    File.Delete(pathDif);
                    File.Delete(pathRef);
                    File.Delete(pathGot);
                }
            }
            finally
            {
                cam.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                diffPixels.Dispose();
                RenderTexture.ReleaseTemporary(renderTarget);
                Object.DestroyImmediate(captureTexture);
                Object.DestroyImmediate(compareTexture);
            }
        }

        [BurstCompile]
        struct DiffImagesJob : IJob
        {
            public NativeArray<Color32> refPixels;
            public NativeArray<Color32> gotPixels;
            public NativeArray<Color32> difPixels;
            [NativeDisableUnsafePtrRestriction] public unsafe float* rmsePtr;
            [NativeDisableUnsafePtrRestriction] public unsafe float* psnrPtr;
            [NativeDisableUnsafePtrRestriction] public unsafe int* difPixCount;

            public unsafe void Execute()
            {
                const int kDiffScale = 5;
                const int kDiffThreshold = 3 * kDiffScale;
                *difPixCount = 0;
                double sumSqDif = 0;
                for (int i = 0; i < refPixels.Length; ++i)
                {
                    Color32 cref = refPixels[i];
                    // note: LoadImage always loads PNGs into ARGB order, so swizzle to normal RGBA
                    cref = new Color32(cref.g, cref.b, cref.a, 255);
                    refPixels[i] = cref;

                    Color32 cgot = gotPixels[i];
                    cgot.a = 255;
                    gotPixels[i] = cgot;

                    Color32 cdif = new Color32(0, 0, 0, 255);
                    cdif.r = (byte)math.abs(cref.r - cgot.r);
                    cdif.g = (byte)math.abs(cref.g - cgot.g);
                    cdif.b = (byte)math.abs(cref.b - cgot.b);
                    sumSqDif += cdif.r * cdif.r + cdif.g * cdif.g + cdif.b * cdif.b;

                    cdif.r = (byte)math.min(255, cdif.r * kDiffScale);
                    cdif.g = (byte)math.min(255, cdif.g * kDiffScale);
                    cdif.b = (byte)math.min(255, cdif.b * kDiffScale);
                    difPixels[i] = cdif;
                    if (cdif.r >= kDiffThreshold || cdif.g >= kDiffThreshold || cdif.b >= kDiffThreshold)
                    {
                        (*difPixCount)++;
                    }
                }

                double meanSqDif = sumSqDif / (refPixels.Length * 3);
                double rmse = math.sqrt(meanSqDif);
                double psnr = 20.0 * math.log10(255.0) - 10.0 * math.log10(rmse * rmse);
                *rmsePtr = (float) rmse;
                *psnrPtr = (float) psnr;
            }
        }
    }
}
