using System;
using UnityEngine;
using UnityEditor;
using UnityEditor.Experimental;
using System.IO;

[CustomPropertyDrawer(typeof(FolderPickerAttribute))]
public class FolderPickerPropertyDrawer : PropertyDrawer
{
    const string kLastPathPref = "nesnausk.utils.FolderPickerLastPath";
    static Texture2D s_FolderIcon => EditorGUIUtility.FindTexture(EditorResources.emptyFolderIconName);
    static readonly int kPathFieldControlID = "FolderPickerPathField".GetHashCode();
    const int kIconSize = 15;

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

    static string PathFieldGUI(Rect position, GUIContent label, string value, string hasToContainFile)
    {
        string displayText = string.IsNullOrWhiteSpace(value) ? "None" : Path.GetFileName(value);

        int controlId = GUIUtility.GetControlID(kPathFieldControlID, FocusType.Keyboard, position);
        Rect dropPosition = EditorGUI.PrefixLabel(position, controlId, label);
        Rect iconRect = new Rect(dropPosition.xMax - kIconSize, dropPosition.y, kIconSize, dropPosition.height);
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
                EditorStyles.objectField.Draw(dropPosition, new GUIContent(displayText), controlId, DragAndDrop.activeControlID == controlId);
                GUI.DrawTexture(iconRect, s_FolderIcon, ScaleMode.ScaleToFit);
                break;
            case EventType.MouseDown:
                if (evt.button != 0 || !GUI.enabled)
                    break;

                if (dropPosition.Contains(evt.mousePosition))
                {
                    if (iconRect.Contains(evt.mousePosition))
                    {
                        if (string.IsNullOrWhiteSpace(value))
                            value = EditorPrefs.GetString(kLastPathPref);
                        string openToPath = string.Empty;
                        if (Directory.Exists(value))
                            openToPath = value;
                        string path = EditorUtility.OpenFolderPanel("Select path", openToPath, "");
                        path = PathAbsToStorage(path);
                        if (CheckPath(path, hasToContainFile))
                        {
                            EditorPrefs.SetString(kLastPathPref, path);
                            value = path;
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
                break;
            case EventType.DragUpdated:
            case EventType.DragPerform:
                if (dropPosition.Contains(evt.mousePosition) && GUI.enabled)
                {
                    if (DragAndDrop.paths.Length > 0)
                    {
                        string path = DragAndDrop.paths[0];
                        path = PathAbsToStorage(path);
                        DragAndDrop.visualMode = DragAndDropVisualMode.Generic;
                        if (CheckPath(path, hasToContainFile))
                        {
                            if (evt.type == EventType.DragPerform)
                            {
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

    /*
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        return EditorGUI.GetPropertyHeight(property, label);
    }
    */

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        var attr = (FolderPickerAttribute) attribute;
        string newAsset = PathFieldGUI(position, label, property.stringValue, attr.hasToContainFile);
        if (GUI.changed)
        {
            property.stringValue = newAsset;
        }
    }
}
