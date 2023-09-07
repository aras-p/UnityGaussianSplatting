using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Experimental;
using System.IO;
using System.Linq;

[CustomPropertyDrawer(typeof(FolderPickerAttribute))]
public class FolderPickerPropertyDrawer : PropertyDrawer
{
    const string kLastPathPref = "nesnausk.utils.FolderPickerLastPath";
    static Texture2D s_FolderIcon => EditorGUIUtility.FindTexture(EditorResources.emptyFolderIconName);
    static GUIStyle s_StyleTextFieldText = new GUIStyle("TextFieldDropDownText");
    static GUIStyle s_StyleTextFieldDropdown = new GUIStyle("TextFieldDropdown");
    static readonly int kPathFieldControlID = "FolderPickerPathField".GetHashCode();
    const int kIconSize = 15;
    const int kRecentPathsCount = 10;

    List<string> m_PreviousPaths;
    GUIContent[] m_PreviousPathsContent;

    void PopulatePreviousPaths(string nameKey)
    {
        if (m_PreviousPaths != null)
            return;

        m_PreviousPaths = new List<string>();
        for (int i = 0; i < kRecentPathsCount; ++i)
        {
            string path = EditorPrefs.GetString($"{kLastPathPref}-{nameKey}-{i}");
            if (!string.IsNullOrWhiteSpace(path))
                m_PreviousPaths.Add(path);
        }

        UpdatePreviousPathsGUIContent();
    }

    void UpdatePreviousPaths(string nameKey, string path)
    {
        m_PreviousPaths ??= new List<string>();

        m_PreviousPaths.Remove(path);
        m_PreviousPaths.Insert(0, path);
        while (m_PreviousPaths.Count > kRecentPathsCount)
            m_PreviousPaths.RemoveAt(m_PreviousPaths.Count - 1);

        UpdatePreviousPathsGUIContent();

        for (int i = 0; i < m_PreviousPaths.Count; ++i)
        {
            EditorPrefs.SetString($"{kLastPathPref}-{nameKey}-{i}", m_PreviousPaths[i]);
        }
    }

    void UpdatePreviousPathsGUIContent()
    {
        m_PreviousPathsContent = m_PreviousPaths.Select(p => new UnityEngine.GUIContent(Path.GetFileName(p))).ToArray();
    }

    static bool CheckPath(string path, string hasToContainFile)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!Directory.Exists(path))
        {
            Debug.LogWarning($"{nameof(FolderPickerAttribute)}: folder {path} does not exist");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(hasToContainFile))
        {
            if (!File.Exists($"{path}/{hasToContainFile}"))
            {
                Debug.LogWarning($"{nameof(FolderPickerAttribute)}: folder {path} does not contain required file {hasToContainFile}");
                return false;
            }
        }
        return true;
    }

    static string PathAbsToStorage(string path)
    {
        path = path.Replace('\\', '/');
        var dataPath = Application.dataPath;
        if (path.StartsWith(dataPath, StringComparison.Ordinal))
        {
            path = Path.GetRelativePath($"{dataPath}/..", path);
            path = path.Replace('\\', '/');
        }
        return path;
    }

    bool CheckAndSetNewPath(ref string path, string nameKey, string hasToContainFile)
    {
        path = PathAbsToStorage(path);
        if (CheckPath(path, hasToContainFile))
        {
            EditorPrefs.SetString(kLastPathPref, path);
            UpdatePreviousPaths(nameKey, path);
            GUI.changed = true;
            Event.current.Use();
            return true;
        }
        return false;
    }

    string PreviousPathsDropdown(Rect position, string value, string nameKey, string hasToContainFile)
    {
        PopulatePreviousPaths(nameKey);

        EditorGUI.BeginDisabledGroup(m_PreviousPathsContent == null || m_PreviousPathsContent.Length == 0);
        EditorGUI.BeginChangeCheck();
        int oldIndent = EditorGUI.indentLevel;
        EditorGUI.indentLevel = 0;
        int parameterIndex = EditorGUI.Popup(position, GUIContent.none, -1, m_PreviousPathsContent, s_StyleTextFieldDropdown);
        if (EditorGUI.EndChangeCheck() && parameterIndex < m_PreviousPaths.Count)
        {
            string newValue = m_PreviousPaths[parameterIndex];
            if (CheckAndSetNewPath(ref newValue, nameKey, hasToContainFile))
                value = newValue;
        }
        EditorGUI.indentLevel = oldIndent;
        EditorGUI.EndDisabledGroup();
        return value;
    }

    string PathFieldGUI(Rect position, GUIContent label, string value, string hasToContainFile, string nameKey)
    {

        int controlId = GUIUtility.GetControlID(kPathFieldControlID, FocusType.Keyboard, position);
        Rect fullRect = EditorGUI.PrefixLabel(position, controlId, label);
        Rect textRect = new Rect(fullRect.x, fullRect.y, fullRect.width - s_StyleTextFieldDropdown.fixedWidth, fullRect.height);
        Rect dropdownRect = new Rect(textRect.xMax, fullRect.y, s_StyleTextFieldDropdown.fixedWidth, fullRect.height);
        Rect iconRect = new Rect(textRect.xMax - kIconSize, textRect.y, kIconSize, textRect.height);

        value = PreviousPathsDropdown(dropdownRect, value, nameKey, hasToContainFile);

        string displayText = string.IsNullOrWhiteSpace(value) ? "None" : Path.GetFileName(value);

        Event evt = Event.current;
        switch (evt.type)
        {
            case EventType.KeyDown:
                if (GUIUtility.keyboardControl == controlId)
                {
                    if (evt.keyCode is KeyCode.Backspace or KeyCode.Delete)
                    {
                        value = null;
                        GUI.changed = true;
                        evt.Use();
                    }
                }
                break;
            case EventType.Repaint:
                s_StyleTextFieldText.Draw(textRect, new GUIContent(displayText), controlId, DragAndDrop.activeControlID == controlId);
                //s_StyleTextFieldDropdown.Draw(dropdownRect, GUIContent.none, controlId, DragAndDrop.activeControlID == controlId);
                GUI.DrawTexture(iconRect, s_FolderIcon, ScaleMode.ScaleToFit);
                break;
            case EventType.MouseDown:
                if (evt.button != 0 || !GUI.enabled)
                    break;

                if (textRect.Contains(evt.mousePosition))
                {
                    if (iconRect.Contains(evt.mousePosition))
                    {
                        if (string.IsNullOrWhiteSpace(value))
                            value = EditorPrefs.GetString(kLastPathPref);
                        string openToPath = string.Empty;
                        if (Directory.Exists(value))
                            openToPath = value;
                        string newPath = EditorUtility.OpenFolderPanel("Select path", openToPath, "");
                        if (CheckAndSetNewPath(ref newPath, nameKey, hasToContainFile))
                        {
                            value = newPath;
                            GUI.changed = true;
                            evt.Use();
                        }
                    }
                    else if (Directory.Exists(value))
                    {
                        EditorUtility.RevealInFinder(value);
                    }
                    GUIUtility.keyboardControl = controlId;
                }

                if (dropdownRect.Contains(evt.mousePosition))
                {

                }
                break;
            case EventType.DragUpdated:
            case EventType.DragPerform:
                if (textRect.Contains(evt.mousePosition) && GUI.enabled)
                {
                    if (DragAndDrop.paths.Length > 0)
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Generic;
                        string path = DragAndDrop.paths[0];
                        path = PathAbsToStorage(path);
                        if (CheckPath(path, hasToContainFile))
                        {
                            if (evt.type == EventType.DragPerform)
                            {
                                UpdatePreviousPaths(nameKey, path);
                                value = path;
                                GUI.changed = true;
                                DragAndDrop.AcceptDrag();
                                DragAndDrop.activeControlID = 0;
                            }
                            else
                                DragAndDrop.activeControlID = controlId;
                        }
                        else
                            DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                        evt.Use();
                    }
                }
                break;
            case EventType.DragExited:
                if (GUI.enabled)
                {
                    HandleUtility.Repaint();
                }
                break;
        }
        return value;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        var attr = (FolderPickerAttribute) attribute;
        string newAsset = PathFieldGUI(position, label, property.stringValue, attr.hasToContainFile, attr.nameKey);
        if (GUI.changed)
        {
            property.stringValue = newAsset;
        }
    }
}
