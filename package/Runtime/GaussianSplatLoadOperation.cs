// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Networking;

namespace GaussianSplatting.Runtime
{
    // Splat loading "operation": does I/O, decompression / encoding jobs and CPU
    // buffers for data upload.
    //
    // Advance the loading on the main thread.
    sealed class GaussianSplatLoadOperation : IDisposable
    {
        enum State { Downloading, Decompressing, Converting, Cameras, Complete }
        State m_State;
        readonly string m_CameraPath;
        UnityWebRequest m_Request;
        Stream m_Stream;
        GZipStream m_Gzip;
        PLYFileReader.Header m_Ply;
        SPZFileReader.Header m_Spz;
        NativeArray<byte> m_Input;
        NativeArray<GaussianSplatPrepareDataJob.BatchResult> m_Results;
        ReadHandle m_Read;
        JobHandle m_Job;
        int m_Start, m_BatchLength;
        float3 m_Min = new(float.PositiveInfinity), m_Max = new(float.NegativeInfinity);
        GaussianSplatData m_Data;
        Thread m_DecompressionThread;
        volatile bool m_CancelDecompression;
        Exception m_DecompressionError;

        public bool isDone => m_State == State.Complete;
        public GaussianSplatCameras.CameraInfo[] cameras { get; private set; } = Array.Empty<GaussianSplatCameras.CameraInfo>();
        public string cameraError { get; private set; }

        public GaussianSplatLoadOperation(string rootFolder, string sourceFile)
        {
            try
            {
                string path = GaussianSplatReader.ResolvePath(rootFolder, sourceFile);
                m_CameraPath = Path.ChangeExtension(path, ".json");
                if (GaussianSplatPaths.IsUrl(path))
                {
                    m_Request = UnityWebRequest.Get(path);
                    m_Request.SendWebRequest();
                    m_State = State.Downloading;
                }
                else
                {
                    m_Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                    ReadHeader();
                }
            }
            catch { Dispose(); throw; }
        }

        public void Update()
        {
            switch (m_State)
            {
                case State.Downloading:
                    if (!m_Request.isDone) return;
                    if (m_Request.result != UnityWebRequest.Result.Success) throw new IOException(m_Request.error);
                    m_Stream = DownloadStream(m_Request.downloadHandler);
                    ReadHeader();
                    break;
                case State.Decompressing:
                    if (m_DecompressionThread.IsAlive) return;
                    m_DecompressionThread.Join();
                    m_DecompressionThread = null;
                    if (m_DecompressionError != null) throw m_DecompressionError;
                    m_Gzip.Dispose();
                    m_Gzip = null;
                    StartConversion();
                    break;
                case State.Converting:
                    if (!m_Job.IsCompleted) return;
                    FinishConversion();
                    if (m_State == State.Cameras) goto case State.Cameras;
                    break;
                case State.Cameras:
                    try { ReadCameras(); }
                    catch (Exception e)
                    {
                        cameraError = $"Could not load optional cameras '{m_CameraPath}': {e.Message}";
                        FinishCameras();
                    }
                    break;
            }
        }

        // transfer ownership once conversion is complete, allowing GPU upload before camera sidecar file finishes
        public GaussianSplatData TakeData()
        {
            if (m_State != State.Cameras && m_State != State.Complete)
                return null;
            var data = m_Data;
            m_Data = null;
            return data;
        }

        unsafe void ReadHeader()
        {
            long start = m_Stream.Position;
            int a = m_Stream.ReadByte(), b = m_Stream.ReadByte();
            m_Stream.Position = start;
            if (a == 0x1f && b == 0x8b) // gzip stream (SPZ)
            {
                m_Gzip = new GZipStream(m_Stream, CompressionMode.Decompress, true);
                m_Spz = SPZFileReader.ReadHeader(m_Gzip);
                m_Data = new GaussianSplatData(m_Spz.count, m_Spz.degree);
                m_Input = new NativeArray<byte>(m_Spz.dataSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                m_Ply.properties = new NativeArray<int>(0, Allocator.Persistent);
                var thread = new Thread(Decompress) { IsBackground = true, Name = "Splat SPZ decompression" };
                thread.Start();
                m_DecompressionThread = thread;
                m_State = State.Decompressing;
            }
            else if (a == 'p' && b == 'l') // PLY
            {
                m_Ply = PLYFileReader.ReadHeader(m_Stream);
                m_Data = new GaussianSplatData(m_Ply.count, m_Ply.degree);
                int capacity = math.min(m_Ply.count, math.max(1, 64 * 1024 * 1024 / m_Ply.stride));
                m_Input = new NativeArray<byte>(capacity * m_Ply.stride, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                StartConversion();
            }
            else throw new InvalidDataException("Expected binary PLY or gzip SPZ (versions 1–3)");
        }

        unsafe void Decompress()
        {
            try
            {
                byte* buffer = (byte*)m_Input.GetUnsafePtr();
                int offset = 0;
                while (offset < m_Input.Length)
                {
                    if (m_CancelDecompression) return;
                    int read = m_Gzip.Read(new Span<byte>(buffer + offset, math.min(m_Input.Length - offset, 65536)));
                    if (read == 0) throw new EndOfStreamException("Truncated SPZ data");
                    offset += read;
                }
                if (!m_CancelDecompression && m_Gzip.ReadByte() != -1) throw new InvalidDataException("Unexpected SPZ trailing data");
            }
            catch (Exception e) { m_DecompressionError = e; }
        }

        unsafe void StartConversion()
        {
            JobHandle dependency = default;
            m_BatchLength = m_Spz.version != 0 ? m_Data.count : math.min(m_Input.Length / m_Ply.stride, m_Data.count - m_Start);
            if (m_Spz.version == 0)
            {
                int bytes = m_BatchLength * m_Ply.stride;
                if (m_Stream is FileStream file)
                {
                    // Read batch from file into native array via async read manager
                    var command = new ReadCommand { Buffer = m_Input.GetUnsafePtr(), Offset = file.Position, Size = bytes };
                    m_Read = AsyncReadManager.Read(file.Name, &command, 1);
                    dependency = m_Read.JobHandle;
                }
                else
                {
                    // URL downloads are already in native memory; copy the next batch without a managed array
                    int offset = 0;
                    while (offset < bytes)
                    {
                        int read = m_Stream.Read(new Span<byte>((byte*)m_Input.GetUnsafePtr() + offset, bytes - offset));
                        if (read == 0) throw new EndOfStreamException("Truncated PLY data");
                        offset += read;
                    }
                }
            }
            
            // prepare data
            int batches = (m_BatchLength + GaussianSplatPrepareDataJob.kBatchSize - 1) / GaussianSplatPrepareDataJob.kBatchSize;
            if (!m_Results.IsCreated)
                m_Results = new NativeArray<GaussianSplatPrepareDataJob.BatchResult>(batches, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var job = new GaussianSplatPrepareDataJob
            {
                input = m_Input, plyPropOffsets = m_Ply.properties, stride = m_Ply.stride,
                count = m_BatchLength, outputStart = m_Start, shCoefficients = m_Data.shCoefficients,
                spzVersion = m_Spz.version, spzTotalCount = m_Data.count, spzFractionalBits = m_Spz.fractionalBits,
                positions = m_Data.positions, geometry = m_Data.geometry, colors = m_Data.colors, sh = m_Data.sh, results = m_Results
            };
            m_Job = job.Schedule(batches, 1, dependency);
            JobHandle.ScheduleBatchedJobs();
            m_State = State.Converting;
        }

        void FinishConversion()
        {
            m_Job.Complete();
            if (m_Read.IsValid())
            {
                if (m_Read.Status != ReadStatus.Complete || m_Read.GetBytesRead() != m_BatchLength * m_Ply.stride)
                    throw new EndOfStreamException("Could not read PLY vertex data");
                m_Read.Dispose();
                m_Read = default;
                m_Stream.Position += m_BatchLength * m_Ply.stride;
            }
            int batches = (m_BatchLength + GaussianSplatPrepareDataJob.kBatchSize - 1) / GaussianSplatPrepareDataJob.kBatchSize;
            for (int i = 0; i < batches; ++i)
            {
                var result = m_Results[i];
                if (result.invalidSplat != 0) throw new InvalidDataException($"Invalid or unrepresentable values at splat {result.invalidSplat - 1}");
                m_Min = math.min(m_Min, result.min);
                m_Max = math.max(m_Max, result.max);
            }
            m_Start += m_BatchLength;
            if (m_Start < m_Data.count) { StartConversion(); return; }
            m_Data.bounds.SetMinMax(m_Min, m_Max);
            DisposeSource();
            m_State = State.Cameras;
        }

        void ReadCameras()
        {
            if (m_Request != null)
            {
                if (!m_Request.isDone) return;
                if (m_Request.result == UnityWebRequest.Result.Success)
                    cameras = GaussianSplatCameras.Parse(m_Request.downloadHandler.text);
            }
            else if (GaussianSplatPaths.IsUrl(m_CameraPath))
            {
                m_Request = UnityWebRequest.Get(m_CameraPath);
                m_Request.SendWebRequest();
                return;
            }
            else if (File.Exists(m_CameraPath))
            {
                cameras = GaussianSplatCameras.Parse(File.ReadAllText(m_CameraPath));
            }
            FinishCameras();
        }

        void FinishCameras()
        {
            m_Request?.Dispose();
            m_Request = null;
            m_State = State.Complete;
        }

        static unsafe UnmanagedMemoryStream DownloadStream(DownloadHandler download)
        {
            var bytes = download.nativeData;
            return new UnmanagedMemoryStream((byte*)bytes.GetUnsafeReadOnlyPtr(), bytes.Length);
        }

        void DisposeSource()
        {
            if (m_Read.IsValid())
            {
                m_Read.JobHandle.Complete();
                m_Read.Dispose();
                m_Read = default;
            }
            // Note: called after conversion, and again when the loading operation is disposed;
            // the IsCreated checks are needed
            if (m_Input.IsCreated) m_Input.Dispose();
            if (m_Results.IsCreated) m_Results.Dispose();

            m_Ply.Dispose();
            m_Gzip?.Dispose();
            m_Gzip = null;
            m_Stream?.Dispose();
            m_Stream = null;
            m_Request?.Dispose();
            m_Request = null;
        }

        public void Dispose()
        {
            m_CancelDecompression = true;
            m_DecompressionThread?.Join();
            m_DecompressionThread = null;
            m_Job.Complete();
            DisposeSource();
            m_Data?.Dispose();
            m_Data = null;
            m_State = State.Complete;
        }
    }
}
