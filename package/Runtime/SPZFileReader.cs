// SPDX-License-Identifier: MIT
using System.IO;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace GaussianSplatting.Runtime
{
    static class SPZFileReader
    {
        public struct Header
        {
            public int version, count, degree, fractionalBits;
            public int streamCount;
            public uint tocOffset;

            public int dataSize => checked(count * ((version == 1 ? 6 : 9) + 7 + (version < 3 ? 3 : 4) + ((degree + 1) * (degree + 1) - 1) * 3));
        }

        public static Header ReadHeader(Stream stream)
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, true);
            if (r.ReadUInt32() != 0x5053474e) throw new InvalidDataException("Invalid SPZ magic");
            uint version = r.ReadUInt32();
            if (version < 1 || version > 4) throw new InvalidDataException($"SPZ version {version} is not supported (supported: 1–4)");
            int count = checked((int)r.ReadUInt32());
            int degree = r.ReadByte(), fractional = r.ReadByte();
            int flags = r.ReadByte(), streamCount = r.ReadByte();
            if (fractional > 24) throw new InvalidDataException("Invalid SPZ fractional bits");
            const int antialiasedFlag = 1;
            if ((flags & ~antialiasedFlag) != 0)
                throw new InvalidDataException($"Unsupported SPZ flags: 0x{flags:X2}");
            if ((flags & antialiasedFlag) != 0)
                UnityEngine.Debug.LogWarning("This SPZ was trained with antialiasing; rendering will not be fully correct");
            uint tocOffset = 0;
            if (version == 4)
            {
                tocOffset = r.ReadUInt32();
                for (int i = 0; i < 3; ++i)
                    if (r.ReadUInt32() != 0) throw new InvalidDataException("Nonzero SPZ reserved header bytes");
                // note: SH stream is omitted for degree zero
                if (streamCount != (degree == 0 ? 5 : 6)) throw new InvalidDataException("Invalid SPZ stream count");
                if (tocOffset < 32 || tocOffset > stream.Length - streamCount * 16)
                    throw new InvalidDataException("Invalid SPZ table offset");
            }
            return new Header { version = (int)version, count = count, degree = degree, fractionalBits = fractional, streamCount = streamCount, tocOffset = tocOffset };
        }

        public struct StreamInfo
        {
            public int inputOffset, inputSize, outputOffset, outputSize;
            public ZstdDecoder.Error error;
        }

        // read the v4 TOC, leaving the stream at the start of the compressed attribute data
        public static NativeArray<StreamInfo> ReadStreams(Stream source, Header header)
        {
            source.Position = header.tocOffset;
            using var reader = new BinaryReader(source, Encoding.UTF8, true);
            var streams = new NativeArray<StreamInfo>(header.streamCount, Allocator.Persistent);
            try
            {
                int inputOffset = 0, outputOffset = 0;
                for (int i = 0; i < streams.Length; ++i)
                {
                    int inputSize = checked((int)reader.ReadUInt64());
                    ulong outputSize = reader.ReadUInt64();
                    int bytesPerSplat = i switch { 0 => 9, 1 => 1, 2 => 3, 3 => 3, 4 => 4, _ => ((header.degree + 1) * (header.degree + 1) - 1) * 3 };
                    int expectedSize = checked(header.count * bytesPerSplat);
                    if (inputSize == 0 || outputSize != (ulong)expectedSize) throw new InvalidDataException("Invalid SPZ stream size");
                    streams[i] = new StreamInfo { inputOffset = inputOffset, inputSize = inputSize, outputOffset = outputOffset, outputSize = expectedSize };
                    inputOffset = checked(inputOffset + inputSize);
                    outputOffset = checked(outputOffset + expectedSize);
                }
                if (inputOffset != source.Length - source.Position) throw new InvalidDataException("SPZ compressed data size mismatch");
                return streams;
            }
            catch { streams.Dispose(); throw; }
        }

        // each v4 attribute is an independent zstd stream.
        // decode directly into the same  layout used by v3, with separate scratch space per stream
        [BurstCompile(CompileSynchronously = true)]
        public struct DecompressJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> input;
            [NativeDisableParallelForRestriction] public NativeArray<byte> output, scratch;
            public NativeArray<StreamInfo> streams;

            public void Execute(int index)
            {
                var stream = streams[index];
                stream.error = ZstdDecoder.Decompress(input.GetSubArray(stream.inputOffset, stream.inputSize),
                    output.GetSubArray(stream.outputOffset, stream.outputSize),
                    scratch.GetSubArray(index * ZstdDecoder.ScratchSize, ZstdDecoder.ScratchSize), out int written);
                if (stream.error == ZstdDecoder.Error.None && written != stream.outputSize)
                    stream.error = ZstdDecoder.Error.InvalidData;
                streams[index] = stream;
            }
        }
    }
}
