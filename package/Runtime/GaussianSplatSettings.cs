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

    public enum TemporalFilter
    {
        None,
        TemporalNoMotion,
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

        [Tooltip("Gaussian splat transparency rendering algorithm")]
        public TransparencyMode m_Transparency = TransparencyMode.SortedBlended;

        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 1;

        [Tooltip("How to filter temporal transparency")]
        public TemporalFilter m_TemporalFilter = TemporalFilter.TemporalNoMotion;
        [Tooltip("How much of new frame to blend in. Higher: more noise, lower: more ghosting.")]
        [Range(0.001f, 1.0f)] public float m_FrameInfluence = 0.05f;
        [Tooltip("Strength of history color rectification clamp. Lower: more flickering, higher: more blur/ghosting.")]
        [Range(0.001f, 10.0f)] public float m_VarianceClampScale = 1.5f;

        public DebugRenderMode m_RenderMode = DebugRenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;

        internal bool isDebugRender => m_RenderMode != DebugRenderMode.Splats;

        internal bool needSorting =>
            (!isDebugRender && m_Transparency == TransparencyMode.SortedBlended) ||
            m_RenderMode == DebugRenderMode.DebugBoxes;

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
