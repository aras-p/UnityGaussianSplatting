// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public static class GaussianSplatCameras
    {
        public struct CameraInfo
        {
            public Vector3 position;
            public Quaternion rotation;
        }

        [Serializable]
        class JsonCamera
        {
            public float[] position;
            public float[][] rotation;
        }

        public static CameraInfo[] Parse(string json)
        {
            var entries = json.FromJson<List<JsonCamera>>();
            if (entries == null) throw new InvalidDataException("Expected a 3DGS camera array");
            var cameras = new CameraInfo[entries.Count];
            for (int i = 0; i < entries.Count; ++i)
            {
                var entry = entries[i];
                if (entry?.position?.Length != 3 || entry.rotation?.Length != 3 ||
                    entry.rotation[0]?.Length != 3 || entry.rotation[1]?.Length != 3 || entry.rotation[2]?.Length != 3)
                    throw new InvalidDataException($"Camera {i} needs a position and 3x3 rotation");

                // Axis convention: matrix columns, with Y and Z flipped.
                var up = -new Vector3(entry.rotation[0][1], entry.rotation[1][1], entry.rotation[2][1]);
                var forward = -new Vector3(entry.rotation[0][2], entry.rotation[1][2], entry.rotation[2][2]);
                cameras[i] = new CameraInfo
                {
                    position = new Vector3(entry.position[0], entry.position[1], entry.position[2]),
                    rotation = Quaternion.LookRotation(forward, up)
                };
            }
            return cameras;
        }
    }
}
