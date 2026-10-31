// SPDX-License-Identifier: MIT
//
// C#/Burst port of TinyZZZ zstd decoder by WangXuan95:
// https://github.com/WangXuan95/TinyZZZ/blob/202721392b/src/zstdD.c (2025 May)
//
// MIT License
// Copyright (c) 2025 https://github.com/WangXuan95
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace GaussianSplatting.Runtime
{
    public static unsafe class ZstdDecoder
    {
        public enum Error { None, InvalidData, UnsupportedDictionary, DestinationTooSmall, ScratchTooSmall }

        const int kBlockSize = 128 * 1024;
        const int kHuffmanBits = 11;
        const int kHuffmanSize = 1 << kHuffmanBits;
        const int kFseSize = 512; // Sequence tables need at most 9 bits; Huffman weights need at most 6.

        struct FseTable
        {
            public fixed byte symbol[kFseSize];
            public fixed byte bits[kFseSize];
            public fixed ushort next[kFseSize];
            public int log;
            public bool exists;
        }

        struct Context
        {
            public fixed ulong offsets[3];
            public fixed byte literals[kBlockSize];
            public fixed byte huffmanSymbol[kHuffmanSize];
            public fixed byte huffmanBits[kHuffmanSize];
            public int huffmanLog;
            public bool hasHuffman;
            public FseTable ll, ml, of;
        }

        public static int ScratchSize => sizeof(Context);

        // Callable from Burst jobs.
        // Buffers must not overlap; each concurrent call needs its own scratch.
        // Output capacity is supplied by the caller.
        public static Error Decompress(NativeArray<byte> input, NativeArray<byte> output, NativeArray<byte> scratch, out int written)
        {
            written = 0;
            if (scratch.Length < ScratchSize) return Error.ScratchTooSmall;
            var decoder = new Decoder
            {
                context = (Context*)scratch.GetUnsafePtr(),
                output = (byte*)output.GetUnsafePtr(),
                capacity = output.Length
            };
            var source = new Input { ptr = (byte*)input.GetUnsafeReadOnlyPtr(), length = input.Length };
            while (source.length > 0 && decoder.error == Error.None)
                decoder.DecodeFrame(ref source);
            written = decoder.written;
            return decoder.error;
        }

        struct Input
        {
            public byte* ptr;
            public int length, bit;
        }

        // entropy streams are read backwards; peeks past the beginning are zero padded
        struct BackwardInput
        {
            public byte* ptr;
            public int length, position;

            public uint Peek(int bits)
            {
                if (bits == 0) return 0;
                int first = position - bits;
                int start = first >> 3, shift = first & 7;
                // read full word in most cases
                if (start >= 0 && start <= length - sizeof(ulong))
                    return (uint)((*(ulong*)(ptr + start) >> shift) & ((1UL << bits) - 1));

                ulong value = 0;
                int bytes = (bits + shift + 7) >> 3;
                for (int i = 0; i < bytes; ++i)
                {
                    int index = start + i;
                    if (index >= 0 && index < length) value |= (ulong)ptr[index] << (i * 8);
                }
                return (uint)((value >> shift) & ((1UL << bits) - 1));
            }

            public uint Read(int bits)
            {
                uint value = Peek(bits);
                position -= bits;
                return value;
            }
        }

        struct Decoder
        {
            public Context* context;
            public byte* output;
            public int capacity, written;
            public Error error;
            int frameStart;
            ulong window;

            bool Fail(Error value = Error.InvalidData)
            {
                if (error == Error.None) error = value;
                return false;
            }

            ulong ReadBytes(ref Input source, int count)
            {
                if (error != Error.None) return 0;
                if (source.bit != 0 || count > source.length) { Fail(); return 0; }
                ulong value = 0;
                for (int i = 0; i < count; ++i) value |= (ulong)source.ptr[i] << (i * 8);
                source.ptr += count;
                source.length -= count;
                return value;
            }

            int ReadBits(ref Input source, int count)
            {
                if (error != Error.None) return 0;
                int bytes = (source.bit + count + 7) >> 3;
                if (bytes > source.length) { Fail(); return 0; }
                uint value = 0;
                for (int i = 0; i < bytes; ++i) value |= (uint)source.ptr[i] << (i * 8);
                value = (value >> source.bit) & ((1u << count) - 1);
                source.bit += count;
                bytes = source.bit >> 3;
                source.ptr += bytes;
                source.length -= bytes;
                source.bit &= 7;
                return (int)value;
            }

            void Align(ref Input source)
            {
                if (source.bit == 0) return;
                source.ptr++;
                source.length--;
                source.bit = 0;
            }

            Input Take(ref Input source, int count)
            {
                if (error != Error.None) return default;
                if (source.bit != 0 || count < 0 || count > source.length) { Fail(); return default; }
                var result = new Input { ptr = source.ptr, length = count };
                source.ptr += count;
                source.length -= count;
                return result;
            }

            BackwardInput Backward(Input source)
            {
                if (error != Error.None) return default;
                if (source.length == 0 || source.ptr[source.length - 1] == 0) { Fail(); return default; }
                return new BackwardInput
                {
                    ptr = source.ptr, length = source.length,
                    position = (source.length - 1) * 8 + HighestBit(source.ptr[source.length - 1])
                };
            }

            public void DecodeFrame(ref Input source)
            {
                ulong magic = ReadBytes(ref source, 4);
                if (error != Error.None) return;
                if (magic >= 0x184D2A50 && magic <= 0x184D2A5F)
                {
                    ulong size = ReadBytes(ref source, 4);
                    if (size > (ulong)source.length) { Fail(); return; }
                    Take(ref source, (int)size);
                    return;
                }
                if (magic != 0xFD2FB528) { Fail(); return; }
                UnsafeUtility.MemClear(context, sizeof(Context));
                context->offsets[0] = 1;
                context->offsets[1] = 4;
                context->offsets[2] = 8;
                frameStart = written;

                int flags = (int)ReadBytes(ref source, 1);
                if ((flags & 8) != 0) { Fail(); return; }
                if ((flags & 3) != 0) { Fail(Error.UnsupportedDictionary); return; }
                bool singleSegment = (flags & 32) != 0;
                window = 0;
                if (!singleSegment)
                {
                    int descriptor = (int)ReadBytes(ref source, 1);
                    ulong windowBase = 1UL << (10 + (descriptor >> 3));
                    window = windowBase + (windowBase >> 3) * (ulong)(descriptor & 7);
                }
                int sizeFlag = flags >> 6;
                bool hasSize = singleSegment || sizeFlag != 0;
                int sizeBytes = sizeFlag == 0 ? 1 : 1 << sizeFlag;
                ulong expected = hasSize ? ReadBytes(ref source, sizeBytes) : 0;
                if (sizeFlag == 1) expected += 256;
                if (singleSegment) window = expected;
                if (hasSize && expected > (ulong)(capacity - written)) { Fail(Error.DestinationTooSmall); return; }
                bool last;
                do
                {
                    int header = (int)ReadBytes(ref source, 3);
                    last = (header & 1) != 0;
                    int type = (header >> 1) & 3, size = header >> 3;
                    if (error != Error.None) return;
                    if (size > kBlockSize || (ulong)size > window) { Fail(); return; }
                    int blockStart = written;
                    if (type == 0)
                    {
                        var raw = Take(ref source, size);
                        if (error != Error.None || !Reserve(size)) return;
                        UnsafeUtility.MemCpy(output + written, raw.ptr, size);
                        written += size;
                    }
                    else if (type == 1)
                    {
                        byte value = (byte)ReadBytes(ref source, 1);
                        if (error != Error.None || !Reserve(size)) return;
                        UnsafeUtility.MemSet(output + written, value, size);
                        written += size;
                    }
                    else if (type == 2)
                    {
                        Input block = Take(ref source, size);
                        int literals = DecodeLiterals(ref block);
                        int sequences = DecodeSequenceTables(ref block);
                        if (error != Error.None) return;
                        DecodeSequences(block, sequences, literals, math.min(kBlockSize, capacity - written));
                    }
                    else { Fail(); return; }
                    if (error != Error.None) return;
                    if (written - blockStart > kBlockSize || (ulong)(written - blockStart) > window) { Fail(); return; }
                } while (!last);
                if ((flags & 4) != 0) Take(ref source, 4); // Checksum is present, but not verified.
                if (hasSize && (ulong)(written - frameStart) != expected) Fail();
            }

            bool Reserve(int count)
            {
                return count <= capacity - written || Fail(Error.DestinationTooSmall);
            }

            int DecodeFrequencies(ref Input source, int* frequency, int log)
            {
                int remaining = (1 << log) + 1, symbols = 0;
                while (remaining > 1 && symbols < 256 && error == Error.None)
                {
                    int bits = HighestBit((ulong)remaining);
                    int value = ReadBits(ref source, bits);
                    int threshold = (1 << (bits + 1)) - 1 - remaining;
                    if (value >= threshold && ReadBits(ref source, 1) != 0)
                        value = (value | (1 << bits)) - threshold;
                    --value;
                    remaining -= math.abs(value);
                    frequency[symbols++] = value;
                    if (value == 0)
                    {
                        int repeat;
                        do
                        {
                            repeat = ReadBits(ref source, 2);
                            if (repeat > 256 - symbols) { Fail(); return 0; }
                            for (int i = 0; i < repeat; ++i) frequency[symbols++] = 0;
                        } while (repeat == 3 && error == Error.None);
                    }
                }
                if (remaining != 1) Fail();
                Align(ref source);
                return symbols;
            }

            void BuildFseTable(FseTable* table, int* frequency, int symbols)
            {
                int* state = stackalloc int[256];
                int limit = 1 << table->log, high = limit, position = 0;
                int step = (limit >> 1) + (limit >> 3) + 3;
                int total = 0;
                for (int s = 0; s < symbols; ++s)
                {
                    total += math.abs(frequency[s]);
                    if (total > limit) { Fail(); return; }
                    if (frequency[s] == -1)
                    {
                        table->symbol[--high] = (byte)s;
                        state[s] = 1;
                    }
                }
                if (total != limit) { Fail(); return; }
                for (int s = 0; s < symbols; ++s)
                {
                    if (frequency[s] <= 0) continue;
                    state[s] = frequency[s];
                    for (int i = 0; i < frequency[s]; ++i)
                    {
                        table->symbol[position] = (byte)s;
                        do { position = (position + step) & (limit - 1); } while (position >= high);
                    }
                }
                if (position != 0) { Fail(); return; }
                for (int i = 0; i < limit; ++i)
                {
                    int next = state[table->symbol[i]]++;
                    int bits = table->log - HighestBit((ulong)next);
                    table->bits[i] = (byte)bits;
                    table->next[i] = (ushort)((next << bits) - limit);
                }
                table->exists = true;
            }

            void DecodeFseTable(FseTable* table, ref Input source, int maxLog)
            {
                table->log = 5 + ReadBits(ref source, 4);
                if (table->log > maxLog) { Fail(); return; }
                int* frequency = stackalloc int[256];
                int symbols = DecodeFrequencies(ref source, frequency, table->log);
                if (error == Error.None) BuildFseTable(table, frequency, symbols);
            }

            int DecodeHuffmanWeights(ref Input source, byte* weights)
            {
                int header = (int)ReadBytes(ref source, 1);
                if (header >= 128)
                {
                    int count = header - 127;
                    for (int i = 0; i < count; i += 2)
                    {
                        int value = (int)ReadBytes(ref source, 1);
                        weights[i] = (byte)(value >> 4);
                        if (i + 1 < count) weights[i + 1] = (byte)(value & 15);
                    }
                    return count;
                }
                Input encoded = Take(ref source, header);
                FseTable table = default;
                DecodeFseTable(&table, ref encoded, 6);
                var bits = Backward(encoded);
                int state1 = (int)bits.Read(table.log), state2 = (int)bits.Read(table.log), countWeights = 0;
                while (error == Error.None)
                {
                    if (countWeights >= 255 || state1 >= (1 << table.log)) { Fail(); return 0; }
                    weights[countWeights++] = table.symbol[state1];
                    if (bits.position < 0) return countWeights;
                    state1 = table.next[state1] + (int)bits.Read(table.bits[state1]);
                    if (countWeights >= 255 || state2 >= (1 << table.log)) { Fail(); return 0; }
                    weights[countWeights++] = table.symbol[state2];
                    if (bits.position < 0) return countWeights;
                    state2 = table.next[state2] + (int)bits.Read(table.bits[state2]);
                }
                return 0;
            }

            void DecodeHuffmanTable(ref Input source)
            {
                byte* weights = stackalloc byte[256];
                int count = DecodeHuffmanWeights(ref source, weights);
                if (error != Error.None) return;
                int sum = 0;
                for (int i = 0; i < count; ++i)
                {
                    if (weights[i] > kHuffmanBits) { Fail(); return; }
                    if (weights[i] != 0) sum += 1 << (weights[i] - 1);
                }
                int log = HighestBit((ulong)sum) + 1;
                int left = (1 << log) - sum;
                if (sum == 0 || log > kHuffmanBits || left == 0 || (left & (left - 1)) != 0) { Fail(); return; }
                weights[count++] = (byte)(HighestBit((ulong)left) + 1);
                int* rankCount = stackalloc int[kHuffmanBits + 1];
                int* rankIndex = stackalloc int[kHuffmanBits + 1];
                UnsafeUtility.MemClear(rankCount, (kHuffmanBits + 1) * sizeof(int));
                for (int i = 0; i < count; ++i)
                {
                    if (weights[i] != 0) weights[i] = (byte)(log + 1 - weights[i]);
                    rankCount[weights[i]]++;
                }
                rankIndex[log] = 0;
                for (int i = log; i >= 1; --i)
                    rankIndex[i - 1] = rankIndex[i] + (rankCount[i] << (log - i));
                if (rankIndex[0] != (1 << log)) { Fail(); return; }
                for (int i = 0; i < count; ++i)
                {
                    int bits = weights[i];
                    if (bits == 0) continue;
                    int start = rankIndex[bits], length = 1 << (log - bits);
                    UnsafeUtility.MemSet(context->huffmanSymbol + start, (byte)i, length);
                    UnsafeUtility.MemSet(context->huffmanBits + start, (byte)bits, length);
                    rankIndex[bits] += length;
                }
                context->huffmanLog = log;
                context->hasHuffman = true;
            }

            void DecodeHuffman(Input source, int count, byte* destination)
            {
                var bits = Backward(source);
                for (int i = 0; i < count && error == Error.None; ++i)
                {
                    int entry = (int)bits.Peek(context->huffmanLog);
                    int used = context->huffmanBits[entry];
                    if (used == 0 || used > bits.position) { Fail(); return; }
                    destination[i] = context->huffmanSymbol[entry];
                    bits.position -= used;
                }
                if (bits.position != 0) Fail();
            }

            int DecodeLiterals(ref Input source)
            {
                int type = ReadBits(ref source, 2), sizeType = ReadBits(ref source, 2);
                int count;
                if (type < 2)
                {
                    count = sizeType == 0 || sizeType == 2
                        ? (ReadBits(ref source, 4) << 1) + (sizeType >> 1)
                        : ReadBits(ref source, sizeType == 1 ? 12 : 20);
                    if (count > kBlockSize) { Fail(); return 0; }
                    if (type == 0)
                    {
                        var raw = Take(ref source, count);
                        if (error == Error.None) UnsafeUtility.MemCpy(context->literals, raw.ptr, count);
                    }
                    else
                    {
                        byte value = (byte)ReadBytes(ref source, 1);
                        if (error == Error.None) UnsafeUtility.MemSet(context->literals, value, count);
                    }
                }
                else
                {
                    int sizeBits = sizeType < 2 ? 10 : sizeType == 2 ? 14 : 18;
                    count = ReadBits(ref source, sizeBits);
                    int compressed = ReadBits(ref source, sizeBits);
                    if (count > kBlockSize) { Fail(); return 0; }
                    Input huffman = Take(ref source, compressed);
                    if (type == 2) DecodeHuffmanTable(ref huffman);
                    else if (!context->hasHuffman) { Fail(); return 0; }
                    if (error != Error.None) return 0;
                    if (sizeType == 0) DecodeHuffman(huffman, count, context->literals);
                    else
                    {
                        int size1 = (int)ReadBytes(ref huffman, 2), size2 = (int)ReadBytes(ref huffman, 2), size3 = (int)ReadBytes(ref huffman, 2);
                        Input a = Take(ref huffman, size1), b = Take(ref huffman, size2), c = Take(ref huffman, size3);
                        int segment = (count + 3) / 4, last = count - segment * 3;
                        if (last < 0) { Fail(); return 0; }
                        DecodeHuffman(a, segment, context->literals);
                        DecodeHuffman(b, segment, context->literals + segment);
                        DecodeHuffman(c, segment, context->literals + segment * 2);
                        DecodeHuffman(huffman, last, context->literals + segment * 3);
                    }
                }
                return count;
            }

            void SequenceTable(FseTable* table, ref Input source, int type, int mode)
            {
                if (mode == 0)
                {
                    int* frequency = stackalloc int[53];
                    int count = type == 0 ? 36 : type == 1 ? 29 : 53;
                    for (int i = 0; i < count; ++i)
                        frequency[i] = type == 0 ? kLiteralFrequency[i] : type == 1 ? kOffsetFrequency[i] : kMatchFrequency[i];
                    table->log = type == 1 ? 5 : 6;
                    BuildFseTable(table, frequency, count);
                }
                else if (mode == 1)
                {
                    table->symbol[0] = (byte)ReadBytes(ref source, 1);
                    table->bits[0] = 0;
                    table->next[0] = 0;
                    table->log = 0;
                    table->exists = true;
                }
                else if (mode == 2) DecodeFseTable(table, ref source, type == 1 ? 8 : 9);
                else if (!table->exists) Fail();
            }

            int DecodeSequenceTables(ref Input source)
            {
                int count = (int)ReadBytes(ref source, 1);
                if (count == 255) count = (int)ReadBytes(ref source, 2) + 0x7F00;
                else if (count >= 128) count = ((count - 128) << 8) + (int)ReadBytes(ref source, 1);
                if (count != 0)
                {
                    int modes = (int)ReadBytes(ref source, 1);
                    if ((modes & 3) != 0) { Fail(); return 0; }
                    SequenceTable(&context->ll, ref source, 0, modes >> 6);
                    SequenceTable(&context->of, ref source, 1, (modes >> 4) & 3);
                    SequenceTable(&context->ml, ref source, 2, (modes >> 2) & 3);
                }
                return count;
            }

            ulong Offset(ulong value, int literals)
            {
                ulong offset = value - 3;
                if (value <= 3)
                {
                    if (literals != 0) --value;
                    offset = value < 3 ? context->offsets[value] : context->offsets[0] - 1;
                }
                if (value != 0)
                {
                    if (value != 1) context->offsets[2] = context->offsets[1];
                    context->offsets[1] = context->offsets[0];
                    context->offsets[0] = offset;
                }
                return offset;
            }

            void DecodeSequences(Input source, int count, int literals, int blockCapacity)
            {
                int blockEnd = written + blockCapacity;
                int literalStart = 0;
                if (count != 0)
                {
                    var bits = Backward(source);
                    int llState = (int)bits.Read(context->ll.log), ofState = (int)bits.Read(context->of.log), mlState = (int)bits.Read(context->ml.log);
                    for (int i = 0; i < count && error == Error.None; ++i)
                    {
                        if (llState >= (1 << context->ll.log) || mlState >= (1 << context->ml.log) || ofState >= (1 << context->of.log)) { Fail(); return; }
                        int llCode = context->ll.symbol[llState], mlCode = context->ml.symbol[mlState], ofCode = context->of.symbol[ofState];
                        if (llCode > 35 || mlCode > 52 || ofCode > 31) { Fail(); return; }
                        ulong offsetCode = (1UL << ofCode) + bits.Read(ofCode);
                        int match = kMatchBase[mlCode] + (int)bits.Read(kMatchBits[mlCode]);
                        int length = kLiteralBase[llCode] + (int)bits.Read(kLiteralBits[llCode]);
                        if (bits.position < 0 || length > literals - literalStart) { Fail(); return; }
                        if (length > blockEnd - written || match > blockEnd - written - length) { Fail(Error.DestinationTooSmall); return; }
                        UnsafeUtility.MemCpy(output + written, context->literals + literalStart, length);
                        written += length;
                        literalStart += length;
                        ulong offset = Offset(offsetCode, length);
                        if (offset == 0 || offset > (ulong)(written - frameStart) || offset > window) { Fail(); return; }
                        // matches may overlap: newly written bytes become the source of later bytes
                        byte* destination = output + written;
                        for (int j = 0; j < match; ++j) destination[j] = destination[j - (int)offset];
                        written += match;
                        if (i + 1 == count) break;
                        llState = context->ll.next[llState] + (int)bits.Read(context->ll.bits[llState]);
                        mlState = context->ml.next[mlState] + (int)bits.Read(context->ml.bits[mlState]);
                        ofState = context->of.next[ofState] + (int)bits.Read(context->of.bits[ofState]);
                    }
                    if (bits.position != 0) { Fail(); return; }
                }
                else if (source.length != 0) { Fail(); return; }
                int remaining = literals - literalStart;
                if (remaining > blockEnd - written) { Fail(Error.DestinationTooSmall); return; }
                UnsafeUtility.MemCpy(output + written, context->literals + literalStart, remaining);
                written += remaining;
            }
        }

        static int HighestBit(ulong value) => 63 - math.lzcnt(value);

        static readonly int[] kLiteralFrequency = { 4,3,2,2,2,2,2,2,2,2,2,2,2,1,1,1,2,2,2,2,2,2,2,2,2,3,2,1,1,1,1,1,-1,-1,-1,-1 };
        static readonly int[] kOffsetFrequency = { 1,1,1,1,1,1,2,2,2,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,-1,-1,-1,-1,-1 };
        static readonly int[] kMatchFrequency = { 1,4,3,2,2,2,2,2,2,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,-1,-1,-1,-1,-1,-1,-1 };
        static readonly int[] kLiteralBase = { 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,18,20,22,24,28,32,40,48,64,128,256,512,1024,2048,4096,8192,16384,32768,65536 };
        static readonly int[] kMatchBase = { 3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,37,39,41,43,47,51,59,67,83,99,131,259,515,1027,2051,4099,8195,16387,32771,65539 };
        static readonly int[] kLiteralBits = { 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1,2,2,3,3,4,6,7,8,9,10,11,12,13,14,15,16 };
        static readonly int[] kMatchBits = { 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1,2,2,3,3,4,4,5,7,8,9,10,11,12,13,14,15,16 };
    }
}
