using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;

public static class PLYFileReader
{
    public static void ReadFile(string filePath, out int vertexCount, out int vertexStride, out List<string> attrNames, out NativeArray<byte> vertices)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        // C# arrays and NativeArrays make it hard to have a "byte" array larger than 2GB :/
        if (fs.Length >= 2 * 1024 * 1024 * 1024L)
            throw new IOException($"PLY {filePath} read error: currently files larger than 2GB are not supported");

        // read header
        vertexCount = 0;
        vertexStride = 0;
        attrNames = new List<string>();
        while (true)
        {
            var line = ReadLine(fs);
            if (line == "end_header")
                break;
            var tokens = line.Split(' ');
            if (tokens.Length == 3 && tokens[0] == "element" && tokens[1] == "vertex")
                vertexCount = int.Parse(tokens[2]);
            if (tokens.Length == 3 && tokens[0] == "property")
            {
                ElementType type = tokens[1] switch
                {
                    "float" => ElementType.Float,
                    "double" => ElementType.Double,
                    "uchar" => ElementType.UChar,
                    _ => ElementType.None
                };
                vertexStride += TypeToSize(type);
                attrNames.Add(tokens[2]);
            }
        }
        //Debug.Log($"PLY {filePath} vtx {vertexCount} stride {vertexStride} attrs #{attrNames.Count} {string.Join(',', attrNames)}");
        vertices = new NativeArray<byte>(vertexCount * vertexStride, Allocator.Persistent);
        var readBytes = fs.Read(vertices);
        if (readBytes != vertices.Length)
            throw new IOException($"PLY {filePath} read error, expected {vertices.Length} data bytes got {readBytes}");
    }

    enum ElementType
    {
        None,
        Float,
        Double,
        UChar
    }

    static int TypeToSize(ElementType t)
    {
        return t switch
        {
            ElementType.None => 0,
            ElementType.Float => 4,
            ElementType.Double => 8,
            ElementType.UChar => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(t), t, null)
        };
    }

    static string ReadLine(FileStream fs)
    {
        var byteBuffer = new List<byte>();
        while (true)
        {
            int b = fs.ReadByte();
            if (b == -1 || b == '\n')
                break;
            byteBuffer.Add((byte)b);
        }
        return Encoding.UTF8.GetString(byteBuffer.ToArray());
    }
}
