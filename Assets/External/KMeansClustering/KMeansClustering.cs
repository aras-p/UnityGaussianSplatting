using System;
using System.Text;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using static Unity.Burst.Intrinsics.X86.Avx;

// Implementation of k-means clustering using k-means++ initial values
[BurstCompile]
public struct KMeansClustering
{
    static ProfilerMarker s_ProfCalculate = new(ProfilerCategory.Render, "KMeans.Calculate", MarkerFlags.SampleGPU);
    static ProfilerMarker s_ProfInitial = new(ProfilerCategory.Render, "KMeans.Initialize", MarkerFlags.SampleGPU);
    static ProfilerMarker s_ProfInitialDistanceSum = new(ProfilerCategory.Render, "KMeans.Initialize.DistanceSum", MarkerFlags.SampleGPU);
    static ProfilerMarker s_ProfInitialPickPoint = new(ProfilerCategory.Render, "KMeans.Initialize.PickPoint", MarkerFlags.SampleGPU);
    static ProfilerMarker s_ProfInitialDistanceUpdate = new(ProfilerCategory.Render, "KMeans.Initialize.DistanceUpdate", MarkerFlags.SampleGPU);
    static ProfilerMarker s_ProfAssignClusters = new(ProfilerCategory.Render, "KMeans.AssignClusters", MarkerFlags.SampleGPU);
    static ProfilerMarker s_ProfUpdateMeans = new(ProfilerCategory.Render, "KMeans.UpdateMeans", MarkerFlags.SampleGPU);
    static ProfilerMarker s_ProfCheckDelta = new(ProfilerCategory.Render, "KMeans.CheckDelta", MarkerFlags.SampleGPU);

    public static int Calculate(int dim, int nth, NativeArray<float> inputData, NativeArray<float> means,
        NativeArray<int> clusters, int maxIterations = 1024, float minDelta = 0.0f, bool debug = false,
        Func<float,bool> progress = null)
    {
        if (dim < 1)
            throw new InvalidOperationException($"{nameof(KMeansClustering)} dimensionality has to be >= 1, was {dim}");
        if (inputData.Length % dim != 0)
            throw new InvalidOperationException(
                $"{nameof(KMeansClustering)} {nameof(inputData)} length must be multiple of {dim}, was {inputData.Length}");
        if (means.Length % dim != 0)
            throw new InvalidOperationException(
                $"{nameof(KMeansClustering)} {nameof(means)} length must be multiple of {dim}, was {means.Length}");
        int dataSize = inputData.Length / dim;
        int k = means.Length / dim;
        if (k < 1)
            throw new InvalidOperationException(
                $"{nameof(KMeansClustering)} {nameof(clusters)} length must be at least 1, was {clusters.Length}");
        if (dataSize < k)
            throw new InvalidOperationException(
                $"{nameof(KMeansClustering)} {nameof(inputData)} length ({inputData.Length}) must at least as long as {nameof(means)} ({means.Length})");

        if (dataSize != clusters.Length)
            throw new InvalidOperationException(
                $"{nameof(KMeansClustering)} {nameof(clusters)} length must be {dataSize}, was {clusters.Length}");

        using var prof = s_ProfCalculate.Auto();

        // Initial means assignment using k-means++
        if (!InitialMeansPP(dim, nth, k, inputData, means, debug, progress))
            return 0;

        NativeArray<float> prevMeans = new(k * dim, Allocator.Persistent);
        NativeArray<float> prevPrevMeans = new(k * dim, Allocator.Persistent);
        AssignClustersJob jobAssignClusters = new AssignClustersJob
        {
            dim = dim,
            data = inputData,
            means = means,
            clusters = clusters
        };
        UpdateMeansJob jobUpdateMeans = new UpdateMeansJob
        {
            dim = dim,
            means = means,
            data = inputData,
            prevMeans = prevMeans,
            clusters = clusters
        };

        int iterCount = 0;
        bool canceled = false;
        do
        {
            const int kAssignBatchCount = 256 * 1024;
            for (int i = 0; i < dataSize; i += kAssignBatchCount)
            {
                if (progress != null && !progress(0.5f + (iterCount + (float)i/dataSize) / maxIterations * 0.5f))
                {
                    canceled = true;
                    break;
                }
                using var profJob = s_ProfAssignClusters.Auto();
                jobAssignClusters.indexOffset = i;
                jobAssignClusters.Schedule(math.min(kAssignBatchCount, dataSize - i), 256).Complete();
            }
            if (canceled)
                break;

            prevPrevMeans.CopyFrom(prevMeans);
            prevMeans.CopyFrom(means);

            {
                using var profJob = s_ProfUpdateMeans.Auto();
                jobUpdateMeans.Schedule().Complete();
            }

            ++iterCount;
        } while (
            iterCount < maxIterations
            && !means.ArraysEqual(prevMeans)
            && !means.ArraysEqual(prevPrevMeans)
            && !DeltaBelowLimit(dim, prevMeans, means, minDelta)
        );

        prevMeans.Dispose();
        prevPrevMeans.Dispose();
        if (canceled)
            iterCount = 0;
        return iterCount;
    }

    static void PrintDebugData(string kind, int dim, NativeArray<float> means, int resultCount, int pointIndex)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < resultCount; ++i)
        {
            sb.Append("(");
            for (int j = 0; j < dim; ++j)
            {
                if (j != 0)
                    sb.Append(", ");
                sb.Append($"{means[i*dim+j]:F3}");
            }
            sb.Append(") ");
        }
        Debug.Log($"Means x{dim} {kind}: point {pointIndex} {sb}");
    }

    static unsafe bool DeltaBelowLimit(int dim, NativeArray<float> a, NativeArray<float> b, float minDelta)
    {
        using var prof = s_ProfCheckDelta.Auto();
        return DeltaBelowLimitImpl(dim, a.Length, (float*) a.GetUnsafeReadOnlyPtr(), (float*) b.GetUnsafeReadOnlyPtr(),
            minDelta);
    }

    [BurstCompile]
    static unsafe bool DeltaBelowLimitImpl(int dim, int length, float* a, float* b, float minDelta)
    {
        if (minDelta <= 0)
            return false;
        float minDeltaSq = minDelta * minDelta;
        for (int i = 0; i < length; i += dim)
        {
            float deltaSq = 0;
            for (var j = 0; j < dim; ++j)
            {
                float ab = a[j] - b[j];
                deltaSq += ab * ab;
            }

            if (deltaSq > minDeltaSq)
                return false;

            a += dim;
            b += dim;
        }

        return true;
    }

    static unsafe float DistanceSquared(int dim, NativeArray<float> a, int aIndex, NativeArray<float> b, int bIndex)
    {
        aIndex *= dim;
        bIndex *= dim;
        float d = 0;
        if (IsAvxSupported)
        {
            // 8x wide with AVX
            int i = 0;
            float* aptr = (float*) a.GetUnsafeReadOnlyPtr() + aIndex;
            float* bptr = (float*) b.GetUnsafeReadOnlyPtr() + bIndex;
            for (; i + 7 < dim; i += 8)
            {
                v256 va = mm256_loadu_ps(aptr);
                v256 vb = mm256_loadu_ps(bptr);
                v256 vd = mm256_sub_ps(va, vb);
                vd = mm256_mul_ps(vd, vd);

                vd = mm256_hadd_ps(vd, vd);
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
    static int PickPointIndex(int dataSize, ref NativeBitArray taken, ref NativeArray<float> minDistSq, float rval)
    {
        int pointIndex = -1;
        float acc = 0.0f;
        for (int i = 0; i < dataSize; ++i)
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

        return pointIndex;
    }

    static bool InitialMeansPP(int dim, int nth, int k, NativeArray<float> inputData, NativeArray<float> means, bool debug, Func<float,bool> progress)
    {
        using var prof = s_ProfInitial.Auto();

        int dataSize = inputData.Length / dim;
        NativeArray<float> nthData = default;
        NativeArray<float> data = inputData;
        if (nth > 1)
        {
            nthData = new NativeArray<float>((dataSize + nth - 1) / nth * dim, Allocator.Persistent);
            dataSize = nthData.Length / dim;
            for (int i = 0; i < dataSize; ++i)
            {
                CopyElem(dim, inputData, i * nth, nthData, i);
            }
            data = nthData;
        }

        NativeBitArray taken = new NativeBitArray(dataSize, Allocator.TempJob);

        // Select first mean randomly
        int pointIndex = (int)(pcg_hash(0) % dataSize);
        taken.Set(pointIndex, true);
        CopyElem(dim, data, pointIndex, means, 0);

        // For each point: closest squared distance to the picked point
        NativeArray<float> minDistSq = new(dataSize, Allocator.TempJob);
        {
            ClosestDistanceInitialJob job = new ClosestDistanceInitialJob
            {
                dim = dim,
                data = data,
                means = means,
                minDistSq = minDistSq,
                pointIndex = pointIndex
            };
            job.Schedule(dataSize, 8 * 1024).Complete();
        }

        int sumBatches = (dataSize + CalcDistSqJob.kBatchSize - 1) / CalcDistSqJob.kBatchSize;
        NativeArray<float> partialSums = new(sumBatches, Allocator.TempJob);
        int resultCount = 1;
        if (debug) PrintDebugData("CPU", dim, means, resultCount, pointIndex);
        bool ok = true;
        while (resultCount < k)
        {
            if (progress != null && !progress((float) resultCount / k * 0.5f))
            {
                ok = false;
                break;
            }

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
                    distSqTotal += partialSums[i];
            }

            // Pick a non-taken point, with a probability proportional
            // to distance: points furthest from any cluster are picked more.
            {
                using var profPart = s_ProfInitialPickPoint.Auto();
                float rval = pcg_hash_float((uint)resultCount, distSqTotal);
                pointIndex = PickPointIndex(dataSize, ref taken, ref minDistSq, rval);
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
            {
                //@TODO: not sure how this could happen?
                break;
            }

            // Take this point as a new cluster mean
            taken.Set(pointIndex, true);
            CopyElem(dim, data, pointIndex, means, resultCount);
            ++resultCount;

            if (debug) PrintDebugData("CPU", dim, means, resultCount, pointIndex);

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
                job.Schedule(dataSize, 8 * 1024).Complete();
            }
        }

        minDistSq.Dispose();
        taken.Dispose();
        partialSums.Dispose();
        nthData.Dispose();
        return ok;
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
        }
    }
    
    // For each means, update them to centroid of points that are assigned to it
    [BurstCompile]
    struct UpdateMeansJob : IJob
    {
        public int dim;
        [ReadOnly] public NativeArray<float> data;
        [ReadOnly] public NativeArray<float> prevMeans;
        [ReadOnly] public NativeArray<int> clusters;
        public NativeArray<float> means;

        public unsafe void Execute()
        {
            int dataSize = data.Length / dim;
            int k = prevMeans.Length / dim;
            // clear cluster sums and counts to zero
            NativeArray<int> counts = new(k, Allocator.Temp);
            UnsafeUtility.MemClear(means.GetUnsafePtr(), means.Length * sizeof(float));

            // sum up cluster values
            int dataOffset = 0;
            for (int i = 0; i < dataSize; ++i)
            {
                int cluster = clusters[i];
                int meanOffset = cluster * dim;
                counts[cluster]++;
                for (int j = 0; j < dim; ++j)
                    means[meanOffset + j] += data[dataOffset + j];
                dataOffset += dim;
            }
            
            // compute means for clusters
            for (int i = 0; i < k; ++i)
            {
                int count = counts[i];
                if (count == 0)
                {
                    // keep from old means
                    CopyElem(dim, prevMeans, i, means, i);
                }
                else
                {
                    // average
                    float invCount = 1.0f / count;
                    for (int j = 0; j < dim; ++j)
                    {
                        means[i * dim + j] *= invCount;
                    }
                }
            }
        }
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
}
