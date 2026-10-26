// SPDX-License-Identifier: MIT
using System.IO;
using System.Text;

namespace GaussianSplatting.Runtime
{
    static class SPZFileReader
    {
        public struct Header
        {
            public int version, count, degree, fractionalBits;

            public int dataSize => checked(count * ((version == 1 ? 6 : 9) + 7 + (version < 3 ? 3 : 4) + ((degree + 1) * (degree + 1) - 1) * 3));
        }

        public static Header ReadHeader(Stream stream)
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, true);
            if (r.ReadUInt32() != 0x5053474e) throw new InvalidDataException("Invalid SPZ magic");
            uint version = r.ReadUInt32();
            if (version < 1 || version > 3) throw new InvalidDataException($"SPZ version {version} is not supported (supported: 1–3)");
            int count = checked((int)r.ReadUInt32());
            int degree = r.ReadByte(), fractional = r.ReadByte();
            int flags = r.ReadByte(); r.ReadByte();
            if (fractional > 24) throw new InvalidDataException("Invalid SPZ fractional bits");
            const int antialiasedFlag = 1;
            if ((flags & ~antialiasedFlag) != 0)
                throw new InvalidDataException($"Unsupported SPZ flags: 0x{flags:X2}");
            if ((flags & antialiasedFlag) != 0)
                UnityEngine.Debug.LogWarning("This SPZ was trained with antialiasing; rendering will not be fully correct");
            return new Header { version = (int)version, count = count, degree = degree, fractionalBits = fractional };
        }
    }
}
