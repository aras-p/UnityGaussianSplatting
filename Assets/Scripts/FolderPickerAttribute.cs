using System;
using UnityEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class FolderPickerAttribute : PropertyAttribute
{
    public string hasToContainFile { get; private set; }

    public FolderPickerAttribute(string hasToContainFile = null)
    {
        this.hasToContainFile = hasToContainFile;
    }
}
