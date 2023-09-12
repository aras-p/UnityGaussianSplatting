using System.IO;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEditor;
using UnityEngine.Experimental.Rendering;

public class GaussianSplatValidator
{
    [MenuItem("Tools/Validate Gaussian Splats")]
    public static void Validate()
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
                gaussians.ActivateCamera(camIndex);
                cam.Render();
                Graphics.SetRenderTarget(renderTarget);
                captureTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);

                byte[] refImageBytes = File.ReadAllBytes($"Images/Ref/Shot-{imageIndex:0000}.png");
                ImageConversion.LoadImage(compareTexture, refImageBytes, false);

                NativeArray<Color32> refPixels = compareTexture.GetPixelData<Color32>(0);
                NativeArray<Color32> gotPixels = captureTexture.GetPixelData<Color32>(0);
                const int kDiffThreshold = 15;
                int errorsCount = 0;
                for (int j = 0; j < width * height; ++j)
                {
                    Color32 cref = refPixels[j];
                    // note: LoadImage always loads PNGs into ARGB order, so swizzle to normal RGBA
                    cref = new Color32(cref.g, cref.b, cref.a, cref.r);
                    cref.a = 255;
                    refPixels[j] = cref;

                    Color32 cgot = gotPixels[j];
                    cgot.a = 255;
                    gotPixels[j] = cgot;

                    Color32 cdif = new Color32(0, 0, 0, 255);
                    cdif.r = (byte)math.min(255, math.abs(cref.r - cgot.r) * 4);
                    cdif.g = (byte)math.min(255, math.abs(cref.r - cgot.r) * 4);
                    cdif.b = (byte)math.min(255, math.abs(cref.r - cgot.r) * 4);
                    diffPixels[j] = cdif;
                    if (cdif.r > kDiffThreshold || cdif.g > kDiffThreshold || cdif.b > kDiffThreshold)
                        ++errorsCount;
                }

                string pathDif = $"Shot-{imageIndex:0000}-diff.png";
                string pathRef = $"Shot-{imageIndex:0000}-ref.png";
                string pathGot = $"Shot-{imageIndex:0000}-got.png";

                if (errorsCount > 10)
                {
                    Debug.LogWarning($"{path} cam {camIndex} (image {imageIndex}) had {errorsCount:N0} different pixels");

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
        RenderTexture.ReleaseTemporary(renderTarget);
        Object.DestroyImmediate(captureTexture);

        Debug.Log("Captured a bunch of shots");
    }
}
