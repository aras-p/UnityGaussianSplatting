// SPDX-License-Identifier: MIT
using System;
using System.IO;

namespace GaussianSplatting.Runtime
{
    public static class GaussianSplatReader
    {
        // relative paths prefer StreamingAssets, then current directory
        public static string ResolvePath(string root, string sourcePath)
        {
            string path = sourcePath ?? "";
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Choose a PLY/SPZ file path or URL");
            bool url = GaussianSplatPaths.IsUrl(path);
            string filePath = path;
            if (url)
            {
                if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)) throw new ArgumentException("Invalid source URL");
                filePath = uri.AbsolutePath;
            }
            else
            {
                path = GaussianSplatPaths.ToForwardSlashes(path);
                filePath = path;
            }
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".ply" && ext != ".spz") throw new ArgumentException("Source must be a .ply or .spz file");
            if (url)
                return path;
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);

            string streamingPath = GaussianSplatPaths.Combine(root, path);
            // StreamingAssets in some builds (Android/Web) require a request to check existence
            if (GaussianSplatPaths.IsUrl(streamingPath) || File.Exists(streamingPath))
                return streamingPath;
            return Path.GetFullPath(path);
        }
    }
}
