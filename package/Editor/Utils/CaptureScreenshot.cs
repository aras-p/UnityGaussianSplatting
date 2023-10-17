// SPDX-License-Identifier: MIT

using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor.Utils
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
