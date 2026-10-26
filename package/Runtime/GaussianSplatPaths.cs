// SPDX-License-Identifier: MIT

using System;
using System.IO;

namespace GaussianSplatting.Runtime
{
    static class GaussianSplatPaths
    {
        public static bool IsUrl(string path)
        {
            return path.Contains("://") || path.StartsWith("jar:");
        }

        public static string Combine(string root, string relativePath)
        {
            if (!IsUrl(root))
                return Path.Combine(root, relativePath);

            string[] parts = relativePath.Split('/');
            for (int i = 0; i < parts.Length; ++i)
                parts[i] = Uri.EscapeDataString(parts[i]);
            return root.TrimEnd('/') + "/" + string.Join('/', parts);
        }
    }
}
