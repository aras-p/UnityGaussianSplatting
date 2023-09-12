using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEditor;
using UnityEngine.Experimental.Rendering;

public class GaussianSplatValidator
{
    [MenuItem("Tools/Validate Gaussian Splats")]
    public static unsafe void Validate()
    {
        var gaussians = Object.FindObjectOfType(typeof(GaussianSplatRenderer)) as GaussianSplatRenderer;
        {
            if (gaussians == null)
            {
                Debug.LogError("No GaussianSplatRenderer object found");
                return;
            }
        }
        var paths = new[]
        {
            "Assets/GaussianAssets/bicycle_30k.asset",
            "Assets/GaussianAssets/truck_30k.asset",
            "Assets/GaussianAssets/playroom_30k.asset",
        };

        int width = 1200;
        int height = 797;

        var cam = Camera.main;
        var oldAsset = gaussians.asset;
        var oldCamPos = cam.transform.position;
        var oldCamRot = cam.transform.rotation;
        var renderTarget = RenderTexture.GetTemporary(width, height, 24, GraphicsFormat.R8G8B8A8_SRGB);
        cam.targetTexture = renderTarget;

        var captureTexture = new Texture2D(width, height, GraphicsFormat.R8G8B8A8_SRGB, TextureCreationFlags.None);
        var compareTexture = new Texture2D(width, height, GraphicsFormat.R8G8B8A8_SRGB, TextureCreationFlags.None);
        NativeArray<Color32> diffPixels = new(width * height, Allocator.Persistent);

        int imageIndex = 1;


        foreach (var path in paths)
        {
            var gs = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(path);
            gaussians.m_Asset = gs;
            gaussians.OnEnable();
            for (int camIndex = 0; camIndex <= 40; camIndex += 10)
            {
                EditorUtility.DisplayProgressBar("Validating Gaussian splat rendering", path, (float)imageIndex / (float)(paths.Length * 5));
                gaussians.ActivateCamera(camIndex);
                cam.Render();
                Graphics.SetRenderTarget(renderTarget);
                captureTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);

                byte[] refImageBytes = File.ReadAllBytes($"Images/Ref/Shot-{imageIndex:0000}.png");
                ImageConversion.LoadImage(compareTexture, refImageBytes, false);

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

                string pathDif = $"Shot-{imageIndex:0000}-diff.png";
                string pathRef = $"Shot-{imageIndex:0000}-ref.png";
                string pathGot = $"Shot-{imageIndex:0000}-got.png";

                if (errorsCount > 50 || psnr < 70.0f)
                {
                    Debug.LogWarning($"{path} cam {camIndex} (image {imageIndex}): RMSE {rmse:F2} PSNR {psnr:F2} diff pixels {errorsCount:N0}");

                    NativeArray<byte> pngBytes = ImageConversion.EncodeNativeArrayToPNG(diffPixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height);
                    File.WriteAllBytes(pathDif, pngBytes.ToArray());
                    pngBytes.Dispose();
                    pngBytes = ImageConversion.EncodeNativeArrayToPNG(refPixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height);
                    File.WriteAllBytes(pathRef, pngBytes.ToArray());
                    pngBytes.Dispose();
                    pngBytes = ImageConversion.EncodeNativeArrayToPNG(gotPixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height);
                    File.WriteAllBytes(pathGot, pngBytes.ToArray());
                    pngBytes.Dispose();
                }
                else
                {
                    File.Delete(pathDif);
                    File.Delete(pathRef);
                    File.Delete(pathGot);
                }

                ++imageIndex;
            }
            gaussians.OnDisable();
        }


        diffPixels.Dispose();

        cam.targetTexture = null;
        gaussians.m_Asset = oldAsset;
        cam.transform.position = oldCamPos;
        cam.transform.rotation = oldCamRot;

        RenderTexture.ReleaseTemporary(renderTarget);
        Object.DestroyImmediate(captureTexture);

        EditorUtility.ClearProgressBar();
        Debug.Log("Captured a bunch of shots");
    }

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
