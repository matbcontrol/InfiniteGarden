using UnityEngine;

namespace InfiniteGarden
{
    /// <summary>
    /// One source of truth for fog and sky. The surface shader, the edge pass and the sky shader
    /// all read these globals, so everything fades into exactly the same color.
    /// </summary>
    [ExecuteAlways]
    public class IGAtmosphere : MonoBehaviour
    {
        public Color horizon = new Color(0.92f, 0.90f, 0.93f);
        public Color zenith = new Color(0.60f, 0.71f, 0.86f);
        public Color nadir = new Color(0.86f, 0.80f, 0.76f);

        [Tooltip("How fast the fog thickens with distance.")]
        [Min(0f)] public float density = 0.0075f;
        [Tooltip("No fog closer than this (m).")]
        [Min(0f)] public float startDistance = 10f;
        [Tooltip("Everything is fully fogged at this distance (m). It is also the view distance of the infinite world.")]
        [Min(1f)] public float endDistance = 300f;
        [Range(0.1f, 4f)] public float gradientSharpness = 0.8f;

        public static IGAtmosphere Active { get; private set; }

        static readonly int s_Horizon = Shader.PropertyToID("_IG_FogHorizon");
        static readonly int s_Zenith = Shader.PropertyToID("_IG_FogZenith");
        static readonly int s_Nadir = Shader.PropertyToID("_IG_FogNadir");
        static readonly int s_Params = Shader.PropertyToID("_IG_FogParams");

        void OnEnable()
        {
            Active = this;
            Apply();
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
        }

        void OnValidate() => Apply();

        void Update() => Apply();

        public void Apply()
        {
            Shader.SetGlobalColor(s_Horizon, horizon);
            Shader.SetGlobalColor(s_Zenith, zenith);
            Shader.SetGlobalColor(s_Nadir, nadir);
            Shader.SetGlobalVector(s_Params, new Vector4(density, startDistance, endDistance, gradientSharpness));
        }
    }
}
