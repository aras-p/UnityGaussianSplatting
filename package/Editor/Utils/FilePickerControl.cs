// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor.Utils
{
    // path field + browse button + project-local recent path history
    public class FilePickerControl
    {
        const int kRecentCount = 20;
        static readonly string kPreference = "nesnausk.GaussianSplatSource." + Hash128.Compute(Application.dataPath);
        static List<string> s_RecentPaths;
        static GUIContent[] s_RecentLabels;

        static void LoadHistory()
        {
            if (s_RecentPaths != null)
                return;
            s_RecentPaths = new List<string>();
            for (int i = 0; i < kRecentCount; ++i)
            {
                string path = EditorPrefs.GetString($"{kPreference}.{i}", "");
                if (!string.IsNullOrEmpty(path))
                    s_RecentPaths.Add(path);
            }
            UpdateLabels();
        }

        static void UpdateLabels()
        {
            s_RecentLabels = new GUIContent[s_RecentPaths.Count];
            for (int i = 0; i < s_RecentPaths.Count; ++i)
                s_RecentLabels[i] = new GUIContent(s_RecentPaths[i]);
        }

        static void RememberPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            s_RecentPaths.Remove(path);
            s_RecentPaths.Insert(0, path);
            if (s_RecentPaths.Count > kRecentCount)
                s_RecentPaths.RemoveAt(kRecentCount);
            for (int i = 0; i < s_RecentPaths.Count; ++i)
                EditorPrefs.SetString($"{kPreference}.{i}", s_RecentPaths[i]);
            UpdateLabels();
        }

        static bool TryRelativePath(string file, out string relative)
        {
            relative = null;
            if (string.IsNullOrWhiteSpace(file))
                return false;
            string root = Path.GetFullPath(Application.streamingAssetsPath).Replace('\\', '/').TrimEnd('/') + "/";
            string full = Path.GetFullPath(file).Replace('\\', '/');
            if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full))
                return false;
            string extension = Path.GetExtension(full).ToLowerInvariant();
            if (extension != ".ply" && extension != ".spz")
                return false;
            relative = full.Substring(root.Length);
            return true;
        }

        string Browse(string value)
        {
            LoadHistory();
            string current = string.IsNullOrEmpty(value) && s_RecentPaths.Count > 0 ? s_RecentPaths[0] : value;
            string directory = Application.streamingAssetsPath;
            if (!string.IsNullOrEmpty(current))
            {
                string candidate = Path.GetDirectoryName(Path.Combine(directory, current));
                if (Directory.Exists(candidate))
                    directory = candidate;
            }
            string file = EditorUtility.OpenFilePanelWithFilters("Select splat file in StreamingAssets", directory, new[] { "Gaussian splats", "ply,spz" });
            if (string.IsNullOrEmpty(file))
                return null;
            if (TryRelativePath(file, out string relative))
            {
                RememberPath(relative);
                return relative;
            }
            EditorUtility.DisplayDialog("Choose a StreamingAssets file", "Choose a PLY/SPZ file inside Assets/StreamingAssets", "OK");
            return null;
        }

        public string PathFieldGUI(Rect position, GUIContent label, string value)
        {
            LoadHistory();
            Rect field = EditorGUI.PrefixLabel(position, label);
            var dropdown = new Rect(field.xMax - 18, field.y, 18, field.height);
            var browse = new Rect(dropdown.x - 22, field.y, 22, field.height);
            var text = new Rect(field.x, field.y, browse.x - field.x, field.height);

            EditorGUI.BeginChangeCheck();
            string typed = EditorGUI.DelayedTextField(text, value ?? "");
            if (EditorGUI.EndChangeCheck())
            {
                value = typed.Replace('\\', '/');
                try
                {
                    string full = GaussianSplatReader.ResolvePath(Application.streamingAssetsPath, value);
                    if (File.Exists(full))
                        RememberPath(value);
                }
                catch (ArgumentException) { } // inspector displays invalid/missing path messages
            }
            if (GUI.Button(browse, EditorGUIUtility.IconContent("Folder Icon", "|Choose a PLY/SPZ file"), EditorStyles.miniButton))
            {
                string selected = Browse(value);
                if (selected != null)
                {
                    value = selected;
                    GUI.changed = true;
                }
            }
            using (new EditorGUI.DisabledScope(s_RecentPaths.Count == 0))
            {
                int selected = EditorGUI.Popup(dropdown, -1, s_RecentLabels, EditorStyles.popup);
                if (selected >= 0)
                {
                    value = s_RecentPaths[selected];
                    RememberPath(value);
                    GUI.changed = true;
                }
            }

            Event evt = Event.current;
            if (field.Contains(evt.mousePosition) && GUI.enabled && evt.type is EventType.DragUpdated or EventType.DragPerform)
            {
                string relative = null;
                bool valid = DragAndDrop.paths.Length == 1 && TryRelativePath(DragAndDrop.paths[0], out relative);
                DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;
                if (valid && evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    value = relative;
                    RememberPath(value);
                    GUI.changed = true;
                }
                evt.Use();
            }
            return value;
        }
    }
}
