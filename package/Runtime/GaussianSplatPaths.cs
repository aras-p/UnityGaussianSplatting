// SPDX-License-Identifier: MIT

using System;
using System.IO;

namespace GaussianSplatting.Runtime
{
    public static class GaussianSplatPaths
    {
        public static string ToForwardSlashes(string path)
        {
            return path.Replace('\\', '/');
        }

        public static bool IsUrl(string path)
        {
            return path.Contains("://") || path.StartsWith("jar:");
        }

        public static string ChangeExtension(string path, string extension)
        {
            int suffix = IsUrl(path) ? path.IndexOfAny(new[] { '?', '#' }) : -1;
            if (suffix < 0)
                return Path.ChangeExtension(path, extension);
            return Path.ChangeExtension(path.Substring(0, suffix), extension) + path.Substring(suffix);
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
