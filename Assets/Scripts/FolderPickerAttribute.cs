using System;
using UnityEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class FolderPickerAttribute : PropertyAttribute
{
    public string nameKey { get; private set; }
    public string hasToContainFile { get; private set; }

    public FolderPickerAttribute(string nameKey, string hasToContainFile = null)
    {
        this.nameKey = nameKey;
        this.hasToContainFile = hasToContainFile;
    }
}
