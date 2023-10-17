// SPDX-License-Identifier: MIT

using UnityEditor;
using UnityEngine;

// ReSharper disable once CheckNamespace
namespace GaussianSplatting.Editor
{
    public class CaptureScreenshot : MonoBehaviour
    {
        [MenuItem("Tools/Gaussian Splats/Debug/Capture Screenshot %g")]
        public static void CaptureShot()
        {
            int counter = 0;
            string path;
            while(true)
            {
                path = $"Shot-{counter:0000}.png";
                if (!System.IO.File.Exists(path))
                    break;
                ++counter;
            }
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log($"Captured {path}");
        }
    }
}
