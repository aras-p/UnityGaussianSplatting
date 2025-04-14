// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public enum TransparencyMode
    {
        // sort splats within each gaussian point cloud, blend back to front
        SortedBlended,
        // no sorting, transparency is stochastic (random) and noisy
        Stochastic,
    }
    public enum DebugRenderMode
    {
        Splats,
        DebugPoints,
        DebugPointIndices,
        DebugBoxes,
        DebugChunkBounds,
    }

    // If an object with this script exists in the scene, then global 3DGS rendering options
    // are used from that script. Otherwise, defaults are used.
    //
    [ExecuteInEditMode] // so that Awake is called in edit mode
    [DefaultExecutionOrder(-100)]
    public class GaussianSplatSettings : MonoBehaviour
    {
        public static GaussianSplatSettings instance
        {
            get
            {
                if (ms_Instance == null)
                    ms_Instance = FindAnyObjectByType<GaussianSplatSettings>();
                if (ms_Instance == null)
                {
                    var go = new GameObject($"{nameof(GaussianSplatSettings)} (Defaults)")
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    ms_Instance = go.AddComponent<GaussianSplatSettings>();
                    ms_Instance.EnsureResources();
                }
                return ms_Instance;
            }
        }
        static GaussianSplatSettings ms_Instance;

        [Header("Options")]
        [Tooltip("Gaussian splat transparency rendering algorithm")]
        public TransparencyMode m_Transparency = TransparencyMode.SortedBlended;

        [Header("Sorted Blended transparency:")]
        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 1;

        [Header("Stochastic transparency:")]
        // Determines how much the history buffer is blended together with current frame result.
        // Lower values means more history contribution, which leads to better anti aliasing,
        // but also more prone to ghosting
        [Range(0.001f, 1.0f)] public float m_TemporalFrameInfluence = 0.05f;
        // Determines the strength of the history color rectification clamp. Lower values can reduce ghosting, but
        // produce more flickering. Higher values reduce flickering, but are prone to blur and ghosting.
        // Between 0.001 - 10.0.
        // Good values around 1.0.
        [Range(0.001f, 10.0f)] public float m_TemporalVarianceClampScale = 1.5f;

        [Header("Debugging Tweaks")]
        public DebugRenderMode m_DebugRenderMode = DebugRenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;

        internal bool needSorting => m_Transparency == TransparencyMode.SortedBlended ||
                                     m_DebugRenderMode == DebugRenderMode.DebugBoxes;

        internal bool resourcesFound { get; private set; }
        bool resourcesLoadAttempted;
        internal Shader shaderSplats { get; private set; }
        internal Shader shaderComposite { get; private set; }
        internal Shader shaderDebugPoints { get; private set; }
        internal Shader shaderDebugBoxes { get; private set; }
        internal ComputeShader csUtilities { get; private set; }

        void Awake()
        {
            if (ms_Instance != null && ms_Instance != this)
                DestroyImmediate(ms_Instance.gameObject);
            ms_Instance = this;
            EnsureResources();
        }

        void EnsureResources()
        {
            if (resourcesLoadAttempted)
                return;
            resourcesLoadAttempted = true;

            shaderSplats = Resources.Load<Shader>("GaussianSplats");
            shaderComposite = Resources.Load<Shader>("GaussianComposite");
            shaderDebugPoints = Resources.Load<Shader>("GaussianDebugRenderPoints");
            shaderDebugBoxes = Resources.Load<Shader>("GaussianDebugRenderBoxes");
            csUtilities = Resources.Load<ComputeShader>("GaussianSplatUtilities");

            resourcesFound =
                shaderSplats != null && shaderComposite != null && shaderDebugPoints != null && shaderDebugBoxes != null &&
                csUtilities != null &&
                SystemInfo.supportsComputeShaders;
            UpdateGlobalOptions();
        }

        void OnValidate()
        {
            UpdateGlobalOptions();
        }

        void OnDidApplyAnimationProperties()
        {
            UpdateGlobalOptions();
        }

        void UpdateGlobalOptions()
        {
            // nothing just yet
        }
    }
}
