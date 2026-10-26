// SPDX-License-Identifier: MIT
using System;
using System.IO;

namespace GaussianSplatting.Runtime
{
    public static class GaussianSplatReader
    {
        public static string ResolvePath(string root, string relativePath)
        {
            string path = (relativePath ?? "").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':'))
                throw new ArgumentException("Choose a PLY/SPZ path relative to StreamingAssets");
            foreach (string part in path.Split('/'))
                if (part == ".." || part == "." || part.Length == 0)
                    throw new ArgumentException("Source path must not contain empty, '.' or '..' segments");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".ply" && ext != ".spz") throw new ArgumentException("Source must be a .ply or .spz file");
            return GaussianSplatPaths.Combine(root, path);
        }
    }
}
