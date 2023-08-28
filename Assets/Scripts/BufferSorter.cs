using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace BufferSorter
{
    public class Sorter : System.IDisposable
    {
        private class Kernels
        {
            public int Init { get; private set; }
            public int Sort { get; private set; }
            public int PadBuffer { get; private set; }
            public int OverwriteAndTruncate { get; private set; }
            public int SetMin { get; private set; }
            public int SetMax { get; private set; }
            public int GetPaddingIndex { get; private set; }
            public int CopyBuffer { get; private set; }

            public Kernels(ComputeShader cs)
            {
                Init = cs.FindKernel("InitKeys");
                Sort = cs.FindKernel("BitonicSort");
                PadBuffer = cs.FindKernel("PadBuffer");
                OverwriteAndTruncate = cs.FindKernel("OverwriteAndTruncate");
                SetMin = cs.FindKernel("SetMin");
                SetMax = cs.FindKernel("SetMax");
                GetPaddingIndex = cs.FindKernel("GetPaddingIndex");
                CopyBuffer = cs.FindKernel("CopyBuffer");
            }
        }

        private static class Properties
        {
            public static int Block { get; private set; } = Shader.PropertyToID("_Block");
            public static int Dimension { get; private set; } = Shader.PropertyToID("_Dimension");
            public static int Count { get; private set; } = Shader.PropertyToID("_Count");
            public static int Reverse { get; private set; } = Shader.PropertyToID("_Reverse");
            public static int NextPowerOfTwo { get; private set; } = Shader.PropertyToID("_NextPowerOfTwo");

            public static int KeysBuffer { get; private set; } = Shader.PropertyToID("_Keys");
            public static int ValuesBuffer { get; private set; } = Shader.PropertyToID("_Values");
            public static int TempBuffer { get; private set; } = Shader.PropertyToID("_Temp");
            public static int PaddingBuffer { get; private set; } = Shader.PropertyToID("_PaddingBuffer");

            public static int ExternalValuesBuffer { get; private set; } = Shader.PropertyToID("_ExternalValues");
            public static int ExternalKeysBuffer { get; private set; } = Shader.PropertyToID("_ExternalKeys");

            public static int FromBuffer { get; private set; } = Shader.PropertyToID("_From");
            public static int ToBuffer { get; private set; } = Shader.PropertyToID("_To");
        }

        private readonly Kernels m_kernels;
        private readonly ComputeShader m_computeShader;

        private ComputeBuffer m_keysBuffer;
        private ComputeBuffer m_tempBuffer;
        private ComputeBuffer m_valuesBuffer;
        private ComputeBuffer m_paddingBuffer;

        private ComputeBuffer m_externalValuesBuffer;
        private ComputeBuffer m_externalKeysBuffer;

        private bool m_mustTruncateValueBuffer = false;
        private bool m_isReverseSort = false;
        private int m_originalCount = 0;
        private int m_paddedCount = 0;

        private readonly int[] m_paddingInput = new int[] { 0, 0 };

        public Sorter(ComputeShader computeShader)
        {
            m_computeShader = computeShader;
            m_kernels = new Kernels(m_computeShader);
        }

        ~Sorter() => Dispose();

        public void Dispose()
        {
            m_keysBuffer?.Dispose();
            m_tempBuffer?.Dispose();
            m_valuesBuffer?.Dispose();
            m_paddingBuffer?.Dispose();
        }

        private void Init(out int x, out int y, out int z)
        {
            Dispose();

            // initializing local buffers
            m_paddingBuffer = new ComputeBuffer(2, sizeof(int));
            m_keysBuffer = new ComputeBuffer(m_paddedCount, sizeof(uint));
            m_tempBuffer = new ComputeBuffer(m_paddedCount, sizeof(int));
            m_valuesBuffer = new ComputeBuffer(m_paddedCount, sizeof(int));

            m_tempBuffer.SetCounterValue(0);
            m_valuesBuffer.SetCounterValue(0);

            m_computeShader.SetInt(Properties.Count, m_originalCount);
            m_computeShader.SetInt(Properties.NextPowerOfTwo, m_paddedCount);

            int minMaxKernel = m_isReverseSort ? m_kernels.SetMin : m_kernels.SetMax;

            m_computeShader.SetBool(Properties.Reverse, m_isReverseSort);

            m_paddingInput[0] = m_isReverseSort ? int.MaxValue : int.MinValue;
            m_paddingInput[1] = 0;

            m_paddingBuffer.SetData(m_paddingInput);

            m_computeShader.SetBuffer(minMaxKernel, Properties.ExternalKeysBuffer, m_externalKeysBuffer);
            m_computeShader.SetBuffer(minMaxKernel, Properties.PaddingBuffer, m_paddingBuffer);

            // first determine either the minimum value or maximum value of the given data, depending on whether it's a reverse sort or not, 
            // to serve as the padding value for non-power-of-two sized inputs
            m_computeShader.Dispatch(minMaxKernel, Mathf.CeilToInt((float)m_originalCount / Util.GROUP_SIZE), 1, 1);

            m_computeShader.SetBuffer(m_kernels.GetPaddingIndex, Properties.ExternalKeysBuffer, m_externalKeysBuffer);
            m_computeShader.SetBuffer(m_kernels.GetPaddingIndex, Properties.PaddingBuffer, m_paddingBuffer);
            m_computeShader.Dispatch(m_kernels.GetPaddingIndex, Mathf.CeilToInt((float)m_originalCount / Util.GROUP_SIZE), 1, 1);
            
            // setting up the second kernel, the padding kernel. because the sort only works on power of two sized buffers,
            // this will pad the buffer with duplicates of the greatest (or least, if reverse sort) integer to be truncated later
            m_computeShader.SetBuffer(m_kernels.PadBuffer, Properties.ExternalKeysBuffer, m_externalKeysBuffer);
            m_computeShader.SetBuffer(m_kernels.PadBuffer, Properties.ExternalValuesBuffer, m_externalValuesBuffer);
            m_computeShader.SetBuffer(m_kernels.PadBuffer, Properties.ValuesBuffer, m_valuesBuffer);
            m_computeShader.SetBuffer(m_kernels.PadBuffer, Properties.PaddingBuffer, m_paddingBuffer);
            m_computeShader.SetBuffer(m_kernels.PadBuffer, Properties.TempBuffer, m_tempBuffer);

            m_computeShader.Dispatch(m_kernels.PadBuffer, Mathf.CeilToInt((float)m_paddedCount / Util.GROUP_SIZE), 1, 1);

            // initialize the keys buffer for use with the sort algorithm proper
            Util.CalculateWorkSize(m_paddedCount, out x, out y, out z);

            m_computeShader.SetInt(Properties.Count, m_paddedCount);

            m_computeShader.SetBuffer(m_kernels.Init, Properties.KeysBuffer, m_keysBuffer);

            m_computeShader.Dispatch(m_kernels.Init, x, y, z);
        }

        /// <summary>
        /// Given a compute buffer of ints or uints, sort the data in-place. Can optionally also sort it in reverse order, or only sort the first n values.
        /// </summary>
        public void Sort(ComputeBuffer values, bool reverse = false, int length = -1)
        {
            ComputeBuffer copyBuff = new ComputeBuffer(values.count, sizeof(int));

            m_computeShader.SetInt(Properties.Count, values.count);
            m_computeShader.SetBuffer(m_kernels.CopyBuffer, Properties.FromBuffer, values);
            m_computeShader.SetBuffer(m_kernels.CopyBuffer, Properties.ToBuffer, copyBuff);

            m_computeShader.Dispatch(m_kernels.CopyBuffer, Mathf.CeilToInt((float)values.count / Util.GROUP_SIZE), 1, 1);

            Sort(values, copyBuff, reverse, length);

            copyBuff.Dispose();
        }
        
        /// <summary>
        /// Given a compute buffer of ints or uints, sort the data in-place. Can optionally also sort it in reverse order, or only sort the first n values.
        /// </summary>
        public void Sort(ComputeBuffer values, ComputeBuffer keys, bool reverse = false, int length = -1)
        {
            Debug.Assert(values.count == keys.count, "Value and key buffers must be of the same size.");

            m_isReverseSort = reverse;
            m_originalCount = length < 0 ? values.count : Mathf.Min(length, values.count);
            m_paddedCount = Mathf.NextPowerOfTwo(m_originalCount);
            m_mustTruncateValueBuffer = !Mathf.IsPowerOfTwo(m_originalCount);
            m_externalValuesBuffer = values;
            m_externalKeysBuffer = keys;

            // initialize the buffers to be used by the sorting algorithm
            Init(out int x, out int y, out int z);

            // run the bitonic merge sort algorithm
            for (int dim = 2; dim <= m_paddedCount; dim <<= 1)
            {
                m_computeShader.SetInt(Properties.Dimension, dim);

                for (int block = dim >> 1; block > 0; block >>= 1)
                {
                    m_computeShader.SetInt(Properties.Block, block);
                    m_computeShader.SetBuffer(m_kernels.Sort, Properties.KeysBuffer, m_keysBuffer);
                    m_computeShader.SetBuffer(m_kernels.Sort, Properties.ValuesBuffer, m_valuesBuffer);

                    m_computeShader.Dispatch(m_kernels.Sort, x, y, z);
                }
            }

            m_computeShader.SetBuffer(m_kernels.OverwriteAndTruncate, Properties.KeysBuffer, m_keysBuffer);
            m_computeShader.SetBuffer(m_kernels.OverwriteAndTruncate, Properties.ExternalValuesBuffer, m_externalValuesBuffer);
            m_computeShader.SetBuffer(m_kernels.OverwriteAndTruncate, Properties.TempBuffer, m_tempBuffer);

            m_computeShader.Dispatch(m_kernels.OverwriteAndTruncate, Mathf.CeilToInt((float)m_originalCount / Util.GROUP_SIZE), 1, 1);

        }

        private static void DebugPrint<T>(ComputeBuffer buffer, string name)
        {
            T[] data = new T[buffer.count];
            buffer.GetData(data);
            Debug.Log(name + ": " + ToFormattedString(data) + " (" + data.Length + ")");
        }

        /// <summary>
        /// Returns a string of the given array in the form [x, y, z...]
        /// </summary>
        public static string ToFormattedString<T>(IList<T> array) => ToFormattedString(array, 0, array.Count);

        /// <summary>
        /// Returns a string of the given array in the form [x, y, z...]
        /// </summary>
        public static string ToFormattedString<T>(IList<T> array, int startIndex) => ToFormattedString(array, startIndex, array.Count);

        /// <summary>
        /// Returns a string of the given array in the form [x, y, z...]
        /// </summary>
        public static string ToFormattedString<T>(IList<T> array, int startIndex, int endIndex)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[");

            endIndex = Mathf.Min(endIndex, array.Count);

            for (int i = startIndex; i < endIndex; i++)
            {
                sb.Append(array[i].ToString());

                if (i < endIndex - 1)
                    sb.Append(", ");
            }

            sb.Append("]");

            return sb.ToString();
        }

        private static class Util
        {
            public const int GROUP_SIZE = 256;
            public const int MAX_DIM_GROUPS = 1024;
            public const int MAX_DIM_THREADS = (GROUP_SIZE * MAX_DIM_GROUPS);

            public static void CalculateWorkSize(int length, out int x, out int y, out int z)
            {
                if (length <= MAX_DIM_THREADS)
                {
                    x = (length - 1) / GROUP_SIZE + 1;
                    y = z = 1;
                }
                else
                {
                    x = MAX_DIM_GROUPS;
                    y = (length - 1) / MAX_DIM_THREADS + 1;
                    z = 1;
                }
            }
        }
    }
}