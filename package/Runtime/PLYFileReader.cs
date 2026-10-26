// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;

namespace GaussianSplatting.Runtime
{
    static class PLYFileReader
    {
        public struct Header : IDisposable
        {
            public int count, degree, stride;
            public NativeArray<int> properties;

            public void Dispose()
            {
                if (properties.IsCreated) properties.Dispose();
            }
        }

        static string ReadLine(Stream stream)
        {
            var line = new StringBuilder();
            for (int i = 0; i < 4096; ++i)
            {
                int c = stream.ReadByte();
                if (c < 0) throw new EndOfStreamException("Incomplete PLY header");
                if (c == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)c);
            }
            throw new InvalidDataException("PLY header line too long");
        }

        public static Header ReadHeader(Stream stream)
        {
            if (ReadLine(stream) != "ply") throw new InvalidDataException("Invalid PLY magic");
            bool binary = false, vertex = false, ended = false, foundVertex = false;
            int count = 0;
            var names = new List<string>();
            var types = new List<string>();
            for (int line = 0; line < 4096; ++line)
            {
                string text = ReadLine(stream);
                string[] t = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0 || t[0] == "comment" || t[0] == "obj_info") continue;
                if (t[0] == "end_header") { ended = true; break; }
                if (t[0] == "format") binary = text == "format binary_little_endian 1.0";
                if (t[0] == "element")
                {
                    if (t.Length != 3) throw new InvalidDataException("Invalid PLY element");
                    vertex = t[1] == "vertex";
                    int n = int.Parse(t[2], CultureInfo.InvariantCulture);
                    if (n < 0) throw new InvalidDataException("Negative PLY element count");
                    if (vertex)
                    {
                        if (foundVertex) throw new InvalidDataException("Duplicate vertex element");
                        foundVertex = true;
                        count = n;
                    }
                    else if (!foundVertex && n != 0) throw new InvalidDataException("PLY vertices must be the first nonempty element");
                }
                if (t[0] == "property" && vertex)
                {
                    if (t.Length != 3 || ScalarSize(t[1]) == 0) throw new InvalidDataException("Unsupported PLY vertex property");
                    if (names.Contains(t[2])) throw new InvalidDataException("Duplicate PLY property: " + t[2]);
                    names.Add(t[2]); types.Add(t[1]);
                }
            }
            if (!binary || !ended || !foundVertex) throw new InvalidDataException("Expected binary_little_endian 1.0 PLY with vertices");
            int Require(string name)
            {
                int i = names.IndexOf(name);
                if (i < 0 || types[i] != "float") throw new InvalidDataException("Missing float Gaussian PLY property: " + name);
                return i;
            }
            string[] required = { "x", "y", "z", "scale_0", "scale_1", "scale_2", "rot_1", "rot_2", "rot_3", "rot_0", "f_dc_0", "f_dc_1", "f_dc_2", "opacity" };
            int rest = names.FindAll(n => n.StartsWith("f_rest_", StringComparison.Ordinal)).Count;
            int degree = rest switch { 0 => 0, 9 => 1, 24 => 2, 45 => 3, _ => throw new InvalidDataException("PLY SH must contain 0, 9, 24 or 45 properties") };
            int stride = 0;
            var offsets = new int[types.Count];
            for (int i = 0; i < types.Count; ++i) { offsets[i] = stride; stride += ScalarSize(types[i]); }
            if (stream.Length - stream.Position < (long)count * stride) throw new EndOfStreamException("Truncated PLY vertex data");
            var properties = new NativeArray<int>(14 + rest, Allocator.Persistent);
            try
            {
                for (int i = 0; i < properties.Length; ++i)
                {
                    int property = Require(i < 14 ? required[i] : "f_rest_" + (i - 14));
                    properties[i] = offsets[property];
                }
                return new Header { count = count, degree = degree, stride = stride, properties = properties };
            }
            catch { properties.Dispose(); throw; }
        }

        static int ScalarSize(string type) => type switch
        {
            "float" => 4,
            "double" => 8,
            "uchar" => 1,
            _ => 0
        };
    }
}
