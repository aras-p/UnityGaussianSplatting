using UnityEditor;
using UnityEngine;

public class CaptureScreenshot : MonoBehaviour
{
    [MenuItem("Tools/Capture Screenshot %g")]
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
