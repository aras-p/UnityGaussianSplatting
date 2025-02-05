// SPDX-License-Identifier: MIT

using System.IO;
using Unity.Collections;
using System.IO.Compression;
using GaussianSplatting.Runtime;
using Unity.Burst;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.Utils
{
    // reads Niantic/Scaniverse .SPZ files:
    // https://github.com/nianticlabs/spz
    // https://scaniverse.com/spz
    [BurstCompile]
    public static class SPZFileReader
    {
        struct SpzHeader {
            public uint magic; // 0x5053474e "NGSP"
            public uint version; // 2
            public uint numPoints;
            public uint sh_fracbits_flags_reserved;
        };
        public static void ReadFileHeader(string filePath, out int vertexCount)
        {
            vertexCount = 0;
            if (!File.Exists(filePath))
                return;
            using var fs = File.OpenRead(filePath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            ReadHeaderImpl(filePath, gz, out vertexCount, out _, out _, out _);
        }

        static void ReadHeaderImpl(string filePath, Stream fs, out int vertexCount, out int shLevel, out int fractBits, out int flags)
        {
            var header = new NativeArray<SpzHeader>(1, Allocator.Temp);
            var readBytes = fs.Read(header.Reinterpret<byte>(16));
            if (readBytes != 16)
                throw new IOException($"SPZ {filePath} read error, failed to read header");

            if (header[0].magic != 0x5053474e)
                throw new IOException($"SPZ {filePath} read error, header magic unexpected {header[0].magic}");
            if (header[0].version != 2)
                throw new IOException($"SPZ {filePath} read error, header version unexpected {header[0].version}");

            vertexCount = (int)header[0].numPoints;
            shLevel = (int)(header[0].sh_fracbits_flags_reserved & 0xFF);
            fractBits = (int)((header[0].sh_fracbits_flags_reserved >> 8) & 0xFF);
            flags = (int)((header[0].sh_fracbits_flags_reserved >> 16) & 0xFF);
        }

        static int SHCoeffsForLevel(int level)
        {
            return level switch
            {
                0 => 0,
                1 => 3,
                2 => 8,
                3 => 15,
                _ => 0
            };
        }

        public static void ReadFile(string filePath, out NativeArray<InputSplatData> splats)
        {
            using var fs = File.OpenRead(filePath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            ReadHeaderImpl(filePath, gz, out var splatCount, out var shLevel, out var fractBits, out var flags);

            if (splatCount < 1 || splatCount > 10_000_000) // 10M hardcoded in SPZ code
                throw new IOException($"SPZ {filePath} read error, out of range splat count {splatCount}");
            if (shLevel < 0 || shLevel > 3)
                throw new IOException($"SPZ {filePath} read error, out of range SH level {shLevel}");
            if (fractBits < 0 || fractBits > 24)
                throw new IOException($"SPZ {filePath} read error, out of range fractional bits {fractBits}");

            // allocate temporary storage
            int shCoeffs = SHCoeffsForLevel(shLevel);
            NativeArray<byte> packedPos = new(splatCount * 3 * 3, Allocator.Persistent);
            NativeArray<byte> packedScale = new(splatCount * 3, Allocator.Persistent);
            NativeArray<byte> packedRot = new(splatCount * 3, Allocator.Persistent);
            NativeArray<byte> packedAlpha = new(splatCount, Allocator.Persistent);
            NativeArray<byte> packedCol = new(splatCount * 3, Allocator.Persistent);
            NativeArray<byte> packedSh = new(splatCount * 3 * shCoeffs, Allocator.Persistent);

            // read file contents into temporaries
            bool readOk = true;
            readOk &= gz.Read(packedPos) == packedPos.Length;
            readOk &= gz.Read(packedAlpha) == packedAlpha.Length;
            readOk &= gz.Read(packedCol) == packedCol.Length;
            readOk &= gz.Read(packedScale) == packedScale.Length;
            readOk &= gz.Read(packedRot) == packedRot.Length;
            readOk &= gz.Read(packedSh) == packedSh.Length;

            // unpack into full splat data
            splats = new NativeArray<InputSplatData>(splatCount, Allocator.Persistent);
            UnpackDataJob job = new UnpackDataJob();
            job.packedPos = packedPos;
            job.packedScale = packedScale;
            job.packedRot = packedRot;
            job.packedAlpha = packedAlpha;
            job.packedCol = packedCol;
            job.packedSh = packedSh;
            job.shCoeffs = shCoeffs;
            job.fractScale = 1.0f / (1 << fractBits);
            job.splats = splats;
            job.Schedule(splatCount, 4096).Complete();

            // cleanup
            packedPos.Dispose();
            packedScale.Dispose();
            packedRot.Dispose();
            packedAlpha.Dispose();
            packedCol.Dispose();
            packedSh.Dispose();

            if (!readOk)
            {
                splats.Dispose();
                throw new IOException($"SPZ {filePath} read error, file smaller than it should be");
            }
        }

        [BurstCompile]
        struct UnpackDataJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedPos;
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedScale;
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedRot;
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedAlpha;
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedCol;
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedSh;
            public float fractScale;
            public int shCoeffs;
            public NativeArray<InputSplatData> splats;

            public void Execute(int index)
            {
                var splat = splats[index];

                splat.pos = new Vector3(UnpackFloat(index * 3 + 0) * fractScale, UnpackFloat(index * 3 + 1) * fractScale, UnpackFloat(index * 3 + 2) * fractScale);

                splat.scale = new Vector3(packedScale[index * 3 + 0], packedScale[index * 3 + 1], packedScale[index * 3 + 2]) / 16.0f - new Vector3(10.0f, 10.0f, 10.0f);
                splat.scale = GaussianUtils.LinearScale(splat.scale);

                Vector3 xyz = new Vector3(packedRot[index * 3 + 0], packedRot[index * 3 + 1], packedRot[index * 3 + 2]) * (1.0f / 127.5f) - new Vector3(1, 1, 1);
                float w = math.sqrt(math.max(0.0f, 1.0f - xyz.sqrMagnitude));
                var q = new float4(xyz.x, xyz.y, xyz.z, w);
                var qq = math.normalize(q);
                qq = GaussianUtils.PackSmallest3Rotation(qq);
                splat.rot = new Quaternion(qq.x, qq.y, qq.z, qq.w);

                splat.opacity = packedAlpha[index] / 255.0f;

                Vector3 col = new Vector3(packedCol[index * 3 + 0], packedCol[index * 3 + 1], packedCol[index * 3 + 2]);
                col = col / 255.0f - new Vector3(0.5f, 0.5f, 0.5f);
                col /= 0.15f;
                splat.dc0 = GaussianUtils.SH0ToColor(col);

                int shIdx = index * shCoeffs * 3;
                splat.sh1 = UnpackSH(shIdx); shIdx += 3;
                splat.sh2 = UnpackSH(shIdx); shIdx += 3;
                splat.sh3 = UnpackSH(shIdx); shIdx += 3;
                splat.sh4 = UnpackSH(shIdx); shIdx += 3;
                splat.sh5 = UnpackSH(shIdx); shIdx += 3;
                splat.sh6 = UnpackSH(shIdx); shIdx += 3;
                splat.sh7 = UnpackSH(shIdx); shIdx += 3;
                splat.sh8 = UnpackSH(shIdx); shIdx += 3;
                splat.sh9 = UnpackSH(shIdx); shIdx += 3;
                splat.shA = UnpackSH(shIdx); shIdx += 3;
                splat.shB = UnpackSH(shIdx); shIdx += 3;
                splat.shC = UnpackSH(shIdx); shIdx += 3;
                splat.shD = UnpackSH(shIdx); shIdx += 3;
                splat.shE = UnpackSH(shIdx); shIdx += 3;
                splat.shF = UnpackSH(shIdx); shIdx += 3;

                splats[index] = splat;
            }

            float UnpackFloat(int idx)
            {
                int fx = packedPos[idx * 3 + 0] | (packedPos[idx * 3 + 1] << 8) | (packedPos[idx * 3 + 2] << 16);
                fx |= (fx & 0x800000) != 0 ? -16777216 : 0; // sign extension with 0xff000000
                return fx;
            }

            Vector3 UnpackSH(int idx)
            {
                Vector3 sh = new Vector3(packedSh[idx], packedSh[idx + 1], packedSh[idx + 2]) - new Vector3(128.0f, 128.0f, 128.0f);
                sh /= 128.0f;
                return sh;
            }
        }

    }
}
