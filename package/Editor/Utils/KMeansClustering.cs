// SPDX-License-Identifier: MIT

using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;

namespace GaussianSplatting.Editor.Utils
{
    // Implementation of "Mini Batch" k-means clustering ("Web-Scale K-Means Clustering", Sculley 2010)
    // using k-means++ for cluster initialization.
    [BurstCompile]
    public struct KMeansClustering
    {
        static ProfilerMarker s_ProfCalculate = new(ProfilerCategory.Render, "KMeans.Calculate", MarkerFlags.SampleGPU);
        static ProfilerMarker s_ProfPlusPlus = new(ProfilerCategory.Render, "KMeans.InitialPlusPlus", MarkerFlags.SampleGPU);
        static ProfilerMarker s_ProfInitialDistanceSum = new(ProfilerCategory.Render, "KMeans.Initialize.DistanceSum", MarkerFlags.SampleGPU);
        static ProfilerMarker s_ProfInitialPickPoint = new(ProfilerCategory.Render, "KMeans.Initialize.PickPoint", MarkerFlags.SampleGPU);
        static ProfilerMarker s_ProfInitialDistanceUpdate = new(ProfilerCategory.Render, "KMeans.Initialize.DistanceUpdate", MarkerFlags.SampleGPU);
        static ProfilerMarker s_ProfAssignClusters = new(ProfilerCategory.Render, "KMeans.AssignClusters", MarkerFlags.SampleGPU);
        static ProfilerMarker s_ProfUpdateMeans = new(ProfilerCategory.Render, "KMeans.UpdateMeans", MarkerFlags.SampleGPU);

        public static bool Calculate(int dim, NativeArray<float> inputData, int batchSize, float passesOverData, Func<float,bool> progress, NativeArray<float> outClusterMeans, NativeArray<int> outDataLabels)
        {
            // Parameter checks
            if (dim < 1)
                throw new InvalidOperationException($"KMeans: dimensionality has to be >= 1, was {dim}");
            if (batchSize < 1)
                throw new InvalidOperationException($"KMeans: batch size has to be >= 1, was {batchSize}");
            if (passesOverData < 0.0001f)
                throw new InvalidOperationException($"KMeans: passes over data must be positive, was {passesOverData}");
            if (inputData.Length % dim != 0)
                throw new InvalidOperationException($"KMeans: input length must be multiple of dim={dim}, was {inputData.Length}");
            if (outClusterMeans.Length % dim != 0)
                throw new InvalidOperationException($"KMeans: output means length must be multiple of dim={dim}, was {outClusterMeans.Length}");
            int dataSize = inputData.Length / dim;
            int k = outClusterMeans.Length / dim;
            if (k < 1)
                throw new InvalidOperationException($"KMeans: cluster count length must be at least 1, was {k}");
            if (dataSize < k)
                throw new InvalidOperationException($"KMeans: input length ({inputData.Length}) must at least as long as clusters ({outClusterMeans.Length})");
            if (dataSize != outDataLabels.Length)
                throw new InvalidOperationException($"KMeans: output labels length must be {dataSize}, was {outDataLabels.Length}");

            using var prof = s_ProfCalculate.Auto();
            batchSize = math.min(dataSize, batchSize);
            uint rngState = 1;

            // Do initial cluster placement
            int initBatchSize = 10 * k;
            const int kInitAttempts = 3;
            if (!InitializeCentroids(dim, inputData, initBatchSize, ref rngState, kInitAttempts, outClusterMeans, progress))
                return false;

            NativeArray<float> counts = new(k, Allocator.TempJob);

            NativeArray<float> batchPoints = new(batchSize * dim, Allocator.TempJob);
            NativeArray<int> batchClusters = new(batchSize, Allocator.TempJob);

            bool cancelled = false;
            for (float calcDone = 0.0f, calcLimit = dataSize * passesOverData; calcDone < calcLimit; calcDone += batchSize)
            {
                if (progress != null && !progress(0.3f + calcDone / calcLimit * 0.4f))
                {
                    cancelled = true;
                    break;
                }

                // generate a batch of random input points
                MakeRandomBatch(dim, inputData, ref rngState, batchPoints);

                // find which of the current centroids each batch point is closest to
                {
                    using var profPart = s_ProfAssignClusters.Auto();
                    AssignClustersJob job = new AssignClustersJob
                    {
                        dim = dim,
                        data = batchPoints,
                        means = outClusterMeans,
                        indexOffset = 0,
                        clusters = batchClusters,
                    };
                    job.Schedule(batchSize, 1).Complete();
                }

                // update the centroids
                {
                    using var profPart = s_ProfUpdateMeans.Auto();
                    UpdateCentroidsJob job = new UpdateCentroidsJob
                    {
                        m_Clusters = outClusterMeans,
                        m_Dim = dim,
                        m_Counts = counts,
                        m_BatchSize = batchSize,
                        m_BatchClusters = batchClusters,
                        m_BatchPoints = batchPoints
                    };
                    job.Schedule().Complete();
                }
            }

            // finally find out closest clusters for all input points
            {
                using var profPart = s_ProfAssignClusters.Auto();
                const int kAssignBatchCount = 256 * 1024;
                AssignClustersJob job = new AssignClustersJob
                {
                    dim = dim,
                    data = inputData,
                    means = outClusterMeans,
                    indexOffset = 0,
                    clusters = outDataLabels,
                };
                for (int i = 0; i < dataSize; i += kAssignBatchCount)
                {
                    if (progress != null && !progress(0.7f + (float) i / dataSize * 0.3f))
                    {
                        cancelled = true;
                        break;
                    }
                    job.indexOffset = i;
                    job.Schedule(math.min(kAssignBatchCount, dataSize - i), 512).Complete();
                }
            }

            counts.Dispose();
            batchPoints.Dispose();
            batchClusters.Dispose();
            return !cancelled;
        }

        static unsafe float DistanceSquared(int dim, NativeArray<float> a, int aIndex, NativeArray<float> b, int bIndex)
        {
            aIndex *= dim;
            bIndex *= dim;
            float d = 0;
            if (X86.Avx.IsAvxSupported)
            {
                // 8x wide with AVX
                int i = 0;
                float* aptr = (float*) a.GetUnsafeReadOnlyPtr() + aIndex;
                float* bptr = (float*) b.GetUnsafeReadOnlyPtr() + bIndex;
                for (; i + 7 < dim; i += 8)
                {
                    v256 va = X86.Avx.mm256_loadu_ps(aptr);
                    v256 vb = X86.Avx.mm256_loadu_ps(bptr);
                    v256 vd = X86.Avx.mm256_sub_ps(va, vb);
                    vd = X86.Avx.mm256_mul_ps(vd, vd);

                    vd = X86.Avx.mm256_hadd_ps(vd, vd);
                    d += vd.Float0 + vd.Float1 + vd.Float4 + vd.Float5;

                    aptr += 8;
                    bptr += 8;
                }
                // remainder
                for (; i < dim; ++i)
                {
                    float delta = *aptr - *bptr;
                    d += delta * delta;
                    aptr++;
                    bptr++;
                }
            }
            else if (Arm.Neon.IsNeonSupported)
            {
                // 4x wide with NEON
                int i = 0;
                float* aptr = (float*) a.GetUnsafeReadOnlyPtr() + aIndex;
                float* bptr = (float*) b.GetUnsafeReadOnlyPtr() + bIndex;
                for (; i + 3 < dim; i += 4)
                {
                    v128 va = Arm.Neon.vld1q_f32(aptr);
                    v128 vb = Arm.Neon.vld1q_f32(bptr);
                    v128 vd = Arm.Neon.vsubq_f32(va, vb);
                    vd = Arm.Neon.vmulq_f32(vd, vd);

                    d += Arm.Neon.vaddvq_f32(vd);

                    aptr += 4;
                    bptr += 4;
                }
                // remainder
                for (; i < dim; ++i)
                {
                    float delta = *aptr - *bptr;
                    d += delta * delta;
                    aptr++;
                    bptr++;
                }

            }
            else
            {
                for (var i = 0; i < dim; ++i)
                {
                    float delta = a[aIndex + i] - b[bIndex + i];
                    d += delta * delta;
                }
            }

            return d;
        }

        static unsafe void CopyElem(int dim, NativeArray<float> src, int srcIndex, NativeArray<float> dst, int dstIndex)
        {
            UnsafeUtility.MemCpy((float*) dst.GetUnsafePtr() + dstIndex * dim,
                (float*) src.GetUnsafeReadOnlyPtr() + srcIndex * dim, dim * 4);
        }

        [BurstCompile]
        struct ClosestDistanceInitialJob : IJobParallelFor
        {
            public int dim;
            [ReadOnly] public NativeArray<float> data;
            [ReadOnly] public NativeArray<float> means;
            public NativeArray<float> minDistSq;
            public int pointIndex;
            public void Execute(int index)
            {
                if (index == pointIndex)
                    return;
                minDistSq[index] = DistanceSquared(dim, data, index, means, 0);
            }
        }

        [BurstCompile]
        struct ClosestDistanceUpdateJob : IJobParallelFor
        {
            public int dim;
            [ReadOnly] public NativeArray<float> data;
            [ReadOnly] public NativeArray<float> means;
            [ReadOnly] public NativeBitArray taken;
            public NativeArray<float> minDistSq;
            public int meanIndex;
            public void Execute(int index)
            {
                if (taken.IsSet(index))
                    return;
                float distSq = DistanceSquared(dim, data, index, means, meanIndex);
                minDistSq[index] = math.min(minDistSq[index], distSq);
            }
        }

        [BurstCompile]
        struct CalcDistSqJob : IJobParallelFor
        {
            public const int kBatchSize = 1024;
            public int dataSize;
            [ReadOnly] public NativeBitArray taken;
            [ReadOnly] public NativeArray<float> minDistSq;
            public NativeArray<float> partialSums;

            public void Execute(int batchIndex)
            {
                int iStart = math.min(batchIndex * kBatchSize, dataSize);
                int iEnd = math.min((batchIndex + 1) * kBatchSize, dataSize);
                float sum = 0;
                for (int i = iStart; i < iEnd; ++i)
                {
                    if (taken.IsSet(i))
                        continue;
                    sum += minDistSq[i];
                }

                partialSums[batchIndex] = sum;
            }
        }

        [BurstCompile]
        static int PickPointIndex(int dataSize, ref NativeArray<float> partialSums, ref NativeBitArray taken, ref NativeArray<float> minDistSq, float rval)
        {
            // Skip batches until we hit the ones that might have value to pick from: binary search for the batch
            int indexL = 0;
            int indexR = partialSums.Length;
            while (indexL < indexR)
            {
                int indexM = (indexL + indexR) / 2;
                if (partialSums[indexM] < rval)
                    indexL = indexM + 1;
                else
                    indexR = indexM;
            }
            float acc = 0.0f;
            if (indexL > 0)
            {
                acc = partialSums[indexL-1];
            }

            // Now search for the needed point
            int pointIndex = -1;
            for (int i = indexL * CalcDistSqJob.kBatchSize; i < dataSize; ++i)
            {
                if (taken.IsSet(i))
                    continue;
                acc += minDistSq[i];
                if (acc >= rval)
                {
                    pointIndex = i;
                    break;
                }
            }

            // If we have not found a point, pick the last available one
            if (pointIndex < 0)
            {
                for (int i = dataSize - 1; i >= 0; --i)
                {
                    if (taken.IsSet(i))
                        continue;
                    pointIndex = i;
                    break;
                }
            }

            if (pointIndex < 0)
                pointIndex = 0;

            return pointIndex;
        }

        static void KMeansPlusPlus(int dim, int k, NativeArray<float> data, NativeArray<float> means, NativeArray<float> minDistSq, ref uint rngState)
        {
            using var prof = s_ProfPlusPlus.Auto();

            int dataSize = data.Length / dim;

            NativeBitArray taken = new NativeBitArray(dataSize, Allocator.TempJob);

            // Select first mean randomly
            int pointIndex = (int)(pcg_random(ref rngState) % dataSize);
            taken.Set(pointIndex, true);
            CopyElem(dim, data, pointIndex, means, 0);

            // For each point: closest squared distance to the picked point
            {
                ClosestDistanceInitialJob job = new ClosestDistanceInitialJob
                {
                    dim = dim,
                    data = data,
                    means = means,
                    minDistSq = minDistSq,
                    pointIndex = pointIndex
                };
                job.Schedule(dataSize, 1024).Complete();
            }

            int sumBatches = (dataSize + CalcDistSqJob.kBatchSize - 1) / CalcDistSqJob.kBatchSize;
            NativeArray<float> partialSums = new(sumBatches, Allocator.TempJob);
            int resultCount = 1;
            while (resultCount < k)
            {
                // Find total sum of distances of not yet taken points
                float distSqTotal = 0;
                {
                    using var profPart = s_ProfInitialDistanceSum.Auto();
                    CalcDistSqJob job = new CalcDistSqJob
                    {
                        dataSize = dataSize,
                        taken = taken,
                        minDistSq = minDistSq,
                        partialSums = partialSums
                    };
                    job.Schedule(sumBatches, 1).Complete();
                    for (int i = 0; i < sumBatches; ++i)
                    {
                        distSqTotal += partialSums[i];
                        partialSums[i] = distSqTotal;
                    }
                }

                // Pick a non-taken point, with a probability proportional
                // to distance: points furthest from any cluster are picked more.
                {
                    using var profPart = s_ProfInitialPickPoint.Auto();
                    float rval = pcg_hash_float(rngState + (uint)resultCount, distSqTotal);
                    pointIndex = PickPointIndex(dataSize, ref partialSums, ref taken, ref minDistSq, rval);
                }

                // Take this point as a new cluster mean
                taken.Set(pointIndex, true);
                CopyElem(dim, data, pointIndex, means, resultCount);
                ++resultCount;

                if (resultCount < k)
                {
                    // Update distances of the points: since it tracks closest one,
                    // calculate distance to the new cluster and update if smaller.
                    using var profPart = s_ProfInitialDistanceUpdate.Auto();
                    ClosestDistanceUpdateJob job = new ClosestDistanceUpdateJob
                    {
                        dim = dim,
                        data = data,
                        means = means,
                        minDistSq = minDistSq,
                        taken = taken,
                        meanIndex = resultCount - 1
                    };
                    job.Schedule(dataSize, 256).Complete();
                }
            }

            taken.Dispose();
            partialSums.Dispose();
        }

        // For each data point, find cluster index that is closest to it
        [BurstCompile]
        struct AssignClustersJob : IJobParallelFor
        {
            public int indexOffset;
            public int dim;
            [ReadOnly] public NativeArray<float> data;
            [ReadOnly] public NativeArray<float> means;
            [NativeDisableParallelForRestriction] public NativeArray<int> clusters;
            [NativeDisableContainerSafetyRestriction] [NativeDisableParallelForRestriction] public NativeArray<float> distances;

            public void Execute(int index)
            {
                index += indexOffset;
                int meansCount = means.Length / dim;
                float minDist = float.MaxValue;
                int minIndex = 0;
                for (int i = 0; i < meansCount; ++i)
                {
                    float dist = DistanceSquared(dim, data, index, means, i);
                    if (dist < minDist)
                    {
                        minIndex = i;
                        minDist = dist;
                    }
                }
                clusters[index] = minIndex;
                if (distances.IsCreated)
                    distances[index] = minDist;
            }
        }

        static void MakeRandomBatch(int dim, NativeArray<float> inputData, ref uint rngState, NativeArray<float> outBatch)
        {
            var job = new MakeBatchJob
            {
                m_Dim = dim,
                m_InputData = inputData,
                m_Seed = pcg_random(ref rngState),
                m_OutBatch = outBatch
            };
            job.Schedule().Complete();
        }

        [BurstCompile]
        struct MakeBatchJob : IJob
        {
            public int m_Dim;
            public NativeArray<float> m_InputData;
            public NativeArray<float> m_OutBatch;
            public uint m_Seed;
            public void Execute()
            {
                uint dataSize = (uint)(m_InputData.Length / m_Dim);
                int batchSize = m_OutBatch.Length / m_Dim;
                NativeHashSet<int> picked = new(batchSize, Allocator.Temp);
                while (picked.Count < batchSize)
                {
                    int index = (int)(pcg_hash(m_Seed++) % dataSize);
                    if (!picked.Contains(index))
                    {
                        CopyElem(m_Dim, m_InputData, index, m_OutBatch, picked.Count);
                        picked.Add(index);
                    }
                }
                picked.Dispose();
            }
        }

        [BurstCompile]
        struct UpdateCentroidsJob : IJob
        {
            public int m_Dim;
            public int m_BatchSize;
            [ReadOnly] public NativeArray<int> m_BatchClusters;
            public NativeArray<float> m_Counts;
            [ReadOnly] public NativeArray<float> m_BatchPoints;
            public NativeArray<float> m_Clusters;

            public void Execute()
            {
                for (int i = 0; i < m_BatchSize; ++i)
                {
                    int clusterIndex = m_BatchClusters[i];
                    m_Counts[clusterIndex]++;
                    float alpha = 1.0f / m_Counts[clusterIndex];

                    for (int j = 0; j < m_Dim; ++j)
                    {
                        m_Clusters[clusterIndex * m_Dim + j] = math.lerp(m_Clusters[clusterIndex * m_Dim + j],
                            m_BatchPoints[i * m_Dim + j], alpha);
                    }
                }
            }
        }

        static bool InitializeCentroids(int dim, NativeArray<float> inputData, int initBatchSize, ref uint rngState, int initAttempts, NativeArray<float> outClusters, Func<float,bool> progress)
        {
            using var prof = s_ProfPlusPlus.Auto();

            int k = outClusters.Length / dim;
            int dataSize = inputData.Length / dim;
            initBatchSize = math.min(initBatchSize, dataSize);

            NativeArray<float> centroidBatch = new(initBatchSize * dim, Allocator.TempJob);
            NativeArray<float> validationBatch = new(initBatchSize * dim, Allocator.TempJob);
            MakeRandomBatch(dim, inputData, ref rngState, centroidBatch);
            MakeRandomBatch(dim, inputData, ref rngState, validationBatch);

            NativeArray<int> tmpIndices = new(initBatchSize, Allocator.TempJob);
            NativeArray<float> tmpDistances = new(initBatchSize, Allocator.TempJob);
            NativeArray<float> curCentroids = new(k * dim, Allocator.TempJob);

            float minDistSum = float.MaxValue;

            bool cancelled = false;
            for (int ia = 0; ia < initAttempts; ++ia)
            {
                if (progress != null && !progress((float) ia / initAttempts * 0.3f))
                {
                    cancelled = true;
                    break;
                }

                KMeansPlusPlus(dim, k, centroidBatch, curCentroids, tmpDistances, ref rngState);

                {
                    using var profPart = s_ProfAssignClusters.Auto();
                    AssignClustersJob job = new AssignClustersJob
                    {
                        dim = dim,
                        data = validationBatch,
                        means = curCentroids,
                        indexOffset = 0,
                        clusters = tmpIndices,
                        distances = tmpDistances
                    };
                    job.Schedule(initBatchSize, 1).Complete();
                }

                float distSum = 0;
                foreach (var d in tmpDistances)
                    distSum += d;

                // is this centroid better?
                if (distSum < minDistSum)
                {
                    minDistSum = distSum;
                    outClusters.CopyFrom(curCentroids);
                }
            }

            centroidBatch.Dispose();
            validationBatch.Dispose();
            tmpDistances.Dispose();
            tmpIndices.Dispose();
            curCentroids.Dispose();
            return !cancelled;
        }

        // https://www.reedbeta.com/blog/hash-functions-for-gpu-rendering/
        static uint pcg_hash(uint input)
        {
            uint state = input * 747796405u + 2891336453u;
            uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
            return (word >> 22) ^ word;
        }

        static float pcg_hash_float(uint input, float upTo)
        {
            uint val = pcg_hash(input);
            float f = math.asfloat(0x3f800000 | (val >> 9)) - 1.0f;
            return f * upTo;
        }

        static uint pcg_random(ref uint rng_state)
        {
            uint state = rng_state;
            rng_state = rng_state * 747796405u + 2891336453u;
            uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
            return (word >> 22) ^ word;
        }
    }
}
