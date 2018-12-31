﻿using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// A potential optimisation in the future would be to allocate scratch space in the job. this isn't supported yet in burst but will be
// https://forum.unity.com/threads/burst-dont-allow-me-to-create-a-nativearray.556105/

namespace Crest
{
    public static class ShapeGerstnerJobs
    {
        // General variables
        public static bool s_initialised = false;
        public static bool s_firstFrame = true;
        public static bool s_jobsRunning = false;

        // Wave data
        static NativeArray<float4> s_wavelengths;
        static NativeArray<float4> s_amps;
        static NativeArray<float4> s_angles;
        static NativeArray<float4> s_phases;
        static NativeArray<float4> s_chopScales;
        static NativeArray<float4> s_gravityScales;

        const int MAX_QUERIES = 4096;

        // Query data for height samples
        static NativeArray<float3> s_queryPositionsHeights;
        static int s_lastQueryIndexHeights = 0;
        static NativeArray<float> s_resultHeights;
        static JobHandle s_handleHeights;
        static Dictionary<int, int2> s_segmentRegistry = new Dictionary<int, int2>();

        /// <summary>
        /// Allocate storage. Should be called once - will assert if called while already initialised.
        /// </summary>
        public static void Init()
        {
            Debug.Assert(s_initialised == false);
            if (s_initialised)
            {
                return;
            }

            s_queryPositionsHeights = new NativeArray<float3>(MAX_QUERIES, Allocator.Persistent);
            s_resultHeights = new NativeArray<float>(MAX_QUERIES, Allocator.Persistent);

            s_segmentRegistry.Clear();
            s_lastQueryIndexHeights = 0;

            s_initialised = true;
        }

        static void CopyFloatsToVectors(float[] inputData, ref NativeArray<float4> outputData)
        {
            int numFloats = inputData.Length;

            // How many vectors required to accommodate length numFloats
            int numVecs = numFloats / 4;
            if (numVecs * 4 < numFloats) numVecs++;

            if (!outputData.IsCreated || outputData.Length != numVecs)
            {
                if (outputData.IsCreated)
                {
                    outputData.Dispose();
                }

                outputData = new NativeArray<float4>(numVecs, Allocator.Persistent);
            }

            // Fill in data
            for (int inputi = 0; inputi < inputData.Length; inputi++)
            {
                int veci = inputi / 4;
                int elemi = inputi % 4;

                // Do the read/write dance as i cant write the data directly
                float4 data = outputData[veci];
                data[elemi] = inputData[inputi];
                outputData[veci] = data;
            }

            // Zero out trailing entries
            for (int remainderi = inputData.Length; remainderi < 4 * numVecs; remainderi++)
            {
                int veci = remainderi / 4;
                int elemi = remainderi % 4;

                // Do the read/write dance as i cant write the data directly
                float4 data = outputData[veci];
                data[elemi] = 0f;
                outputData[veci] = data;
            }
        }

        static void CopyPerOctaveFloatsToVectors(float[] perOctaveInputData, int componentsPerOctave, ref NativeArray<float4> outputData)
        {
            int numFloats = perOctaveInputData.Length * componentsPerOctave;

            // How many vectors required to accommodate length numFloats
            int numVecs = numFloats / 4;
            if (numVecs * 4 < numFloats) numVecs++;

            if (!outputData.IsCreated || outputData.Length != numVecs)
            {
                if (outputData.IsCreated)
                {
                    outputData.Dispose();
                }

                outputData = new NativeArray<float4>(numVecs, Allocator.Persistent);
            }

            // Fill the data - repeat per octave data componentsPerOctave times
            int veci = 0;
            int elemi = 0;
            for (int octavei = 0; octavei < perOctaveInputData.Length; octavei++)
            {
                for (int compi = 0; compi < componentsPerOctave; compi++)
                {
                    // Do the read/write dance as i cant write the data directly
                    float4 data = outputData[veci];
                    data[elemi] = perOctaveInputData[octavei];
                    outputData[veci] = data;

                    elemi = (elemi + 1) % 4;
                    if (elemi == 0) veci++;
                }
            }

            // Zero out trailing entries
            while (veci < numVecs)
            {
                // Do the read/write dance as i cant write the data directly
                float4 data = outputData[veci];
                data[elemi] = 0f;
                outputData[veci] = data;

                elemi = (elemi + 1) % 4;
                if (elemi == 0) veci++;
            }
        }

        /// <summary>
        /// Set the Gerstner wave data. Will reallocate data if the number of waves changes.
        /// </summary>
        public static void SetWaveData(float[] wavelengths, float[] amps, float[] angles, float[] phases, float[] chopScales, float[] gravityScales, int compPerOctave)
        {
            CopyFloatsToVectors(wavelengths, ref s_wavelengths);
            CopyFloatsToVectors(amps, ref s_amps);
            CopyFloatsToVectors(angles, ref s_angles);
            CopyFloatsToVectors(phases, ref s_phases);

            CopyPerOctaveFloatsToVectors(chopScales, compPerOctave, ref s_chopScales);
            CopyPerOctaveFloatsToVectors(gravityScales, compPerOctave, ref s_gravityScales);
        }

        /// <summary>
        /// Dispose storage
        /// </summary>
        public static void Cleanup()
        {
            s_initialised = false;

            s_handleHeights.Complete();

            s_wavelengths.Dispose();
            s_amps.Dispose();
            s_angles.Dispose();
            s_phases.Dispose();
            s_chopScales.Dispose();
            s_gravityScales.Dispose();

            s_queryPositionsHeights.Dispose();
            s_resultHeights.Dispose();
        }

        /// <summary>
        /// Updates the query positions (creates space for them the first time). If the query count doesnt match a new set of query
        /// position data will be created. This will force any running jobs to complete.
        /// </summary>
        /// <returns>True if successful.</returns>
        public static bool UpdateQueryPoints(int guid, float3[] queryPoints)
        {
            // Call this in case the user has not called it.
            CompleteJobs();

            // Get segment
            var segmentRetrieved = false;
            int2 querySegment;
            if (s_segmentRegistry.TryGetValue(guid, out querySegment))
            {
                // make sure segment size matches our query count
                var segmentSize = querySegment[1] - querySegment[0];
                if (segmentSize == queryPoints.Length)
                {
                    // All good
                    segmentRetrieved = true;
                }
                else
                {
                    // Query count does not match segment - remove it. The segment will be recreated below.
                    s_segmentRegistry.Remove(guid);
                }
            }

            // If no segment was retrieved, add one if there is space
            if (!segmentRetrieved)
            {
                if (s_lastQueryIndexHeights + queryPoints.Length > MAX_QUERIES)
                {
                    Debug.LogError("Out of query data space. Try calling Compact() to reorganise query segments.");
                    return false;
                }

                querySegment = new int2(s_lastQueryIndexHeights, s_lastQueryIndexHeights + queryPoints.Length);
                s_segmentRegistry.Add(guid, querySegment);
                s_lastQueryIndexHeights += queryPoints.Length;
            }

            // Save off the query data
            for (var i = querySegment.x; i < querySegment.y; i++)
            {
                s_queryPositionsHeights[i] = queryPoints[i - querySegment.x];
            }

            return true;
        }

        /// <summary>
        /// Signal that the query storage for a particular guid is no longer required. This will leave air bubbles in the buffer -
        /// call CompactQueryStorage() to reorganise.
        /// </summary>
        public static void RemoveQueryPoints(int guid)
        {
            if (s_segmentRegistry.ContainsKey(guid))
            {
                s_segmentRegistry.Remove(guid);
            }
        }

        /// <summary>
        /// Change segment IDs to make them contiguous. This will invalidate any jobs and job results!
        /// </summary>
        public static void CompactQueryStorage()
        {
            // Make sure jobs are not running
            CompleteJobs();

            // A bit sneaky but just clear the registry. Will force segments to recreate which achieves the desired effect.
            s_segmentRegistry.Clear();
            s_lastQueryIndexHeights = 0;
        }

        /// <summary>
        /// Retrieve result data from jobs.
        /// </summary>
        /// <returns>True if data returned, false if failed</returns>
        public static bool RetrieveResultHeights(int guid, ref float[] outHeights)
        {
            var segment = new int2(0, 0);
            if (!s_segmentRegistry.TryGetValue(guid, out segment))
            {
                return false;
            }

            s_resultHeights.Slice(segment.x, segment.y - segment.x).CopyTo(outHeights);
            return true;
        }

        /// <summary>
        /// Run the jobs
        /// </summary>
        /// <returns>True if jobs kicked off, false if jobs already running.</returns>
        public static bool ScheduleJobs()
        {
            if (s_jobsRunning)
            {
                return false;
            }

            if (s_lastQueryIndexHeights == 0)
            {
                // Nothing to do
                return true;
            }

            s_jobsRunning = true;

            var heightJob = new HeightJob()
            {
                _wavelengths = s_wavelengths,
                _amps = s_amps,
                _angles = s_angles,
                _phases = s_phases,
                _chopScales = s_chopScales,
                _gravityScales = s_gravityScales,
                _queryPositions = s_queryPositionsHeights,
                // TODO - segment could effect both the min wavelength and also unused waves at the end?
                _computeSegment = new int2(0, s_queryPositionsHeights.Length),
                _time = OceanRenderer.Instance.CurrentTime,
                _globalWindAngle = OceanRenderer.Instance._windDirectionAngle,
                _outHeights = s_resultHeights,
                _seaLevel = OceanRenderer.Instance.SeaLevel,
            };

            s_handleHeights = heightJob.Schedule(s_lastQueryIndexHeights, 32);

            JobHandle.ScheduleBatchedJobs();

            s_firstFrame = false;

            return true;
        }

        /// <summary>
        /// Ensure that jobs are completed. Blocks until complete.
        /// </summary>
        public static void CompleteJobs()
        {
            if (!s_firstFrame && s_jobsRunning)
            {
                s_handleHeights.Complete();
                s_jobsRunning = false;
            }
        }

        /// <summary>
        /// This returns the vertical component of the wave displacement at a position.
        /// </summary>
        [BurstCompile]
        public struct VerticalDisplacementJob : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<float4> _wavelengths;
            [ReadOnly]
            public NativeArray<float4> _amps;
            [ReadOnly]
            public NativeArray<float4> _angles;
            [ReadOnly]
            public NativeArray<float4> _phases;
            [ReadOnly]
            public NativeArray<float4> _chopScales;
            [ReadOnly]
            public NativeArray<float4> _gravityScales;

            [ReadOnly]
            public NativeArray<float3> _queryPositions;

            [WriteOnly]
            public NativeArray<float> _outHeights;

            [ReadOnly]
            public float _time;
            [ReadOnly]
            public float _globalWindAngle;
            [ReadOnly]
            public int2 _computeSegment;

            public void Execute(int iinput)
            {
                if (iinput >= _computeSegment.x && iinput < _computeSegment.y - _computeSegment.x)
                {
                    float resultHeight = 0f;
                    float4 twoPi = 2f * Mathf.PI;
                    float4 g_over_2pi = 9.81f / twoPi;

                    for (var iwavevec = 0; iwavevec < _wavelengths.Length; iwavevec++)
                    {
                        // Wave speed
                        float4 C = math.sqrt(g_over_2pi * _wavelengths[iwavevec] * _gravityScales[iwavevec]);

                        // Wave direction
                        float4 angle = (_globalWindAngle + _angles[iwavevec]) * Mathf.Deg2Rad;
                        float4 Dx = math.cos(angle);
                        float4 Dz = math.sin(angle);

                        // Wave number
                        float4 k = twoPi / _wavelengths[iwavevec];

                        float4 x = Dx * _queryPositions[iinput].x + Dz * _queryPositions[iinput].z;
                        float4 t = k * (x + C * _time) + _phases[iwavevec];

                        resultHeight += math.csum(_amps[iwavevec] * math.cos(t));
                    }

                    _outHeights[iinput] = resultHeight;
                }
            }
        }

        /// <summary>
        /// This inverts the displacement to get the true water height at a position.
        /// </summary>
        [BurstCompile]
        public struct HeightJob : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<float4> _wavelengths;
            [ReadOnly]
            public NativeArray<float4> _amps;
            [ReadOnly]
            public NativeArray<float4> _angles;
            [ReadOnly]
            public NativeArray<float4> _phases;
            [ReadOnly]
            public NativeArray<float4> _chopScales;
            [ReadOnly]
            public NativeArray<float4> _gravityScales;

            [ReadOnly]
            public NativeArray<float3> _queryPositions;

            [WriteOnly]
            public NativeArray<float> _outHeights;

            [ReadOnly]
            public float _time;
            [ReadOnly]
            public float _globalWindAngle;
            [ReadOnly]
            public int2 _computeSegment;
            [ReadOnly]
            public float _seaLevel;

            float3 ComputeDisplacement(float3 queryPos)
            {
                float4 twoPi = 2f * Mathf.PI;
                float4 g_over_2pi = 9.81f / twoPi;

                float3 displacement = 0f;

                for (var iwavevec = 0; iwavevec < _wavelengths.Length; iwavevec++)
                {
                    // Wave speed
                    float4 C = math.sqrt(g_over_2pi * _wavelengths[iwavevec] * _gravityScales[iwavevec]);

                    // Wave direction
                    float4 angle = (_globalWindAngle + _angles[iwavevec]) * Mathf.Deg2Rad;
                    float4 Dx = math.cos(angle);
                    float4 Dz = math.sin(angle);

                    // Wave number
                    float4 k = twoPi / _wavelengths[iwavevec];

                    float4 x = Dx * queryPos.x + Dz * queryPos.z;
                    float4 t = k * (x + C * _time) + _phases[iwavevec];

                    displacement.y += math.csum(_amps[iwavevec] * math.cos(t));

                    float4 disp = -_chopScales[iwavevec] * math.sin(t);
                    displacement.x += math.csum(_amps[iwavevec] * Dx * disp);
                    displacement.z += math.csum(_amps[iwavevec] * Dz * disp);
                }

                return displacement;
            }

            public void Execute(int iinput)
            {
                if (iinput >= _computeSegment.x && iinput < _computeSegment.y - _computeSegment.x)
                {
                    // This could be even faster if i could allocate scratch space to store intermediate calculation results (not supported by burst yet)

                    float3 undisplacedPos = _queryPositions[iinput];
                    undisplacedPos.y = 0f;

                    for (int iter = 0; iter < 4; iter++)
                    {
                        float3 disp = ComputeDisplacement(undisplacedPos);

                        // Correct the undisplaced position - goal is to find the position that displaces to the query position
                        float2 error = undisplacedPos.xz + disp.xz - _queryPositions[iinput].xz;
                        undisplacedPos.xz -= error;
                    }

                    // Our height is now the displacement at the final pos
                    float3 displacement = ComputeDisplacement(undisplacedPos);

                    _outHeights[iinput] = displacement.y + _seaLevel;
                }
            }
        }
    }
}