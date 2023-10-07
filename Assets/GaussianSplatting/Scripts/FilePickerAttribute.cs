// SPDX-License-Identifier: MIT
using System;
using UnityEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class FilePickerAttribute : PropertyAttribute
{
    public string nameKey { get; }
    public string extension { get; }

    public FilePickerAttribute(string nameKey, string extension = null)
    {
        this.nameKey = nameKey;
        this.extension = extension;
    }
}
