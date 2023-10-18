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

        static unsafe void ValidateImpl(string refPrefix)
        {
            var gaussians = Object.FindObjectOfType(typeof(GaussianSplatRenderer)) as GaussianSplatRenderer;
            {
                if (gaussians == null)
                {
                    Debug.LogError("No GaussianSplatRenderer object found");
                    return;
                }
            }
            var items = new RefItem[]
            {
                new() {assetPath = "bicycle", cameraIndex = 0, fov = 39.09651f},
                new() {assetPath = "truck", cameraIndex = 30, fov = 50},
                new() {assetPath = "garden", cameraIndex = 30, fov = 47},
            };

            var cam = Camera.main;
            var oldAsset = gaussians.asset;
            var oldCamPos = cam.transform.localPosition;
            var oldCamRot = cam.transform.localRotation;
            var oldCamFov = cam.fieldOfView;

            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                EditorUtility.DisplayProgressBar("Validating Gaussian splat rendering", item.assetPath, (float)index / items.Length);
                var path = $"Assets/GaussianAssets/{item.assetPath}-point_cloud-iteration_30000-point_cloud.asset";
                var gs = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(path);
                if (gs == null)
                {
                    Debug.LogError($"Did not find asset for validation item {item.assetPath} at {path}");
                    continue;
                }
                var refImageFile = $"../../docs/RefImages/{refPrefix}_{item.assetPath}{item.cameraIndex}.png"; // use our snapshot by default
                if (!File.Exists(refImageFile))
                {
                    Debug.LogError($"Did not find reference image for validation item {item.assetPath} at {refImageFile}");
                    continue;
                }

                var compareTexture = new Texture2D(4, 4, GraphicsFormat.R8G8B8A8_SRGB, TextureCreationFlags.None);
                byte[] refImageBytes = File.ReadAllBytes(refImageFile);
                ImageConversion.LoadImage(compareTexture, refImageBytes, false);

                int width = compareTexture.width;
                int height = compareTexture.height;

                var renderTarget = RenderTexture.GetTemporary(width, height, 24, GraphicsFormat.R8G8B8A8_SRGB);
                cam.targetTexture = renderTarget;
                cam.fieldOfView = item.fov;

                var captureTexture = new Texture2D(width, height, GraphicsFormat.R8G8B8A8_SRGB, TextureCreationFlags.None);
                NativeArray<Color32> diffPixels = new(width * height, Allocator.Persistent);

                gaussians.m_Asset = gs;
                gaussians.Update();
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

                diffPixels.Dispose();
                RenderTexture.ReleaseTemporary(renderTarget);
                Object.DestroyImmediate(captureTexture);
                Object.DestroyImmediate(compareTexture);
            }

            cam.targetTexture = null;
            gaussians.m_Asset = oldAsset;
            gaussians.Update();
            cam.transform.localPosition = oldCamPos;
            cam.transform.localRotation = oldCamRot;
            cam.fieldOfView = oldCamFov;

            EditorUtility.ClearProgressBar();
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
