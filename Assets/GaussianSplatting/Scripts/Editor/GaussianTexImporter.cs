using System;
using System.IO;
using Unity.Collections;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[ScriptedImporter(1, "gstex", AllowCaching = true)]
public class GaussianTexImporter : ScriptedImporter
{
    const uint kMagic = 0x58545347u; // GSTX
    const uint kMaxTexSize = 16 * 1024;

    public override void OnImportAsset(AssetImportContext ctx)
    {
        NativeArray<byte> data = default;
        try
        {
            using var fs = new FileStream(ctx.assetPath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);
            uint magic = br.ReadUInt32();
            int width = br.ReadInt32();
            int height = br.ReadInt32();
            int format = br.ReadInt32();
            ulong dataHash = br.ReadUInt64();
            if (magic != kMagic || width < 1 || width > kMaxTexSize || height < 1 || height > kMaxTexSize ||
                format < 1 ||
                format > (int) GraphicsFormat.D16_UNorm_S8_UInt)
            {
                ctx.LogImportError($"Invalid data in '{ctx.assetPath}' file header");
                return;
            }

            GraphicsFormat gfxFormat = (GraphicsFormat) format;
            int dataSize = (int)GraphicsFormatUtility.ComputeMipmapSize(width, height, gfxFormat);
            data = new NativeArray<byte>((int)dataSize, Allocator.Persistent);
            int bytesRead = br.Read(data);
            if (bytesRead != dataSize)
            {
                ctx.LogImportError($"File '{ctx.assetPath}' did not contain enough data: {width}x{height} {gfxFormat} needs {dataSize:N0}, read {bytesRead:N0} bytes");
                return;
            }

            var tex = new Texture2D(width, height, gfxFormat, TextureCreationFlags.None);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Point;
            tex.anisoLevel = 0;
            tex.imageContentsHash = new Hash128(dataHash, ((uint)width) | ((ulong)height << 16) | ((ulong)format << 32));
            tex.SetPixelData(data, 0);
            tex.Apply(false, true); // make non-readable

            ctx.AddObjectToAsset(Path.GetFileNameWithoutExtension(ctx.assetPath), tex);
            ctx.SetMainObject(tex);
        }
        catch (Exception ex)
        {
            ctx.LogImportError($"Error importing '{ctx.assetPath}': {ex.Message}");
        }
        finally
        {
            data.Dispose();
        }
    }

    public static void WriteAsset(int width, int height, GraphicsFormat format, ReadOnlySpan<byte> data, ulong dataHash, string path)
    {
        int dataSize = (int)GraphicsFormatUtility.ComputeMipmapSize(width, height, format);
        if (data.Length != dataSize)
            throw new InvalidOperationException($"Could not write '{path}': {width}x{height} {format} needs {dataSize:N0}, have {data.Length:N0} bytes");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var br = new BinaryWriter(fs);
        br.Write(kMagic);
        br.Write(width);
        br.Write(height);
        br.Write((int)format);
        br.Write(dataHash);
        br.Write(data);
    }
}
