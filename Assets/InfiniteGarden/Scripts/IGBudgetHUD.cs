using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

namespace InfiniteGarden
{
    /// <summary>
    /// On-screen frame budget: real GPU and CPU frame time from FrameTimingManager, plus hotkeys that switch
    /// each feature off, so its cost can be read as a delta in milliseconds.
    /// GPU time needs Project Settings > Player > Other Settings > Frame Timing Stats.
    /// </summary>
    public class IGBudgetHUD : MonoBehaviour
    {
        public IGWorldRepeater world;
        public IGAtmosphere atmosphere;
        [Tooltip("The renderer asset that holds the IG Edge and SSAO features (PC_Renderer in the URP template).")]
        public UniversalRendererData rendererData;
        public Material edgeMaterial;
        public float budgetMs = 16.67f;
        public bool visible = true;

        static readonly int s_TapCount = Shader.PropertyToID("_TapCount");
        static readonly int s_DebugEdges = Shader.PropertyToID("_DebugEdges");
        static readonly int[] s_TapSteps = { 4, 8, 20 };

        readonly FrameTiming[] m_Timing = new FrameTiming[1];
        readonly StringBuilder m_Text = new StringBuilder(1024);
        float m_GpuMs, m_CpuMs;
        ProfilerRecorder m_DrawCalls;
        ScriptableRendererFeature m_Edges, m_Ssao;
        GUIStyle m_Style;

        // Settings live in assets: remember them and restore on exit, so play mode does not leave changes behind.
        bool m_EdgesWasActive, m_SsaoWasActive;
        float m_TapsWas, m_DebugWas, m_EndDistanceWas;

        void OnEnable()
        {
            m_DrawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");

            if (rendererData != null)
            {
                foreach (ScriptableRendererFeature feature in rendererData.rendererFeatures)
                {
                    if (feature is IGEdgeFeature) m_Edges = feature;
                    else if (feature is ScreenSpaceAmbientOcclusion) m_Ssao = feature;
                }
            }

            if (m_Edges != null) m_EdgesWasActive = m_Edges.isActive;
            if (m_Ssao != null) m_SsaoWasActive = m_Ssao.isActive;
            if (edgeMaterial != null)
            {
                m_TapsWas = edgeMaterial.GetFloat(s_TapCount);
                m_DebugWas = edgeMaterial.GetFloat(s_DebugEdges);
            }
            if (atmosphere != null) m_EndDistanceWas = atmosphere.endDistance;
        }

        void OnDisable()
        {
            m_DrawCalls.Dispose();
            if (m_Edges != null) m_Edges.SetActive(m_EdgesWasActive);
            if (m_Ssao != null) m_Ssao.SetActive(m_SsaoWasActive);
            if (edgeMaterial != null)
            {
                edgeMaterial.SetFloat(s_TapCount, m_TapsWas);
                edgeMaterial.SetFloat(s_DebugEdges, m_DebugWas);
            }
            if (atmosphere != null) atmosphere.endDistance = m_EndDistanceWas;
        }

        void Update()
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, m_Timing) > 0)
            {
                // Light smoothing so the numbers are readable on video.
                m_GpuMs = Mathf.Lerp(m_GpuMs, (float)m_Timing[0].gpuFrameTime, 0.1f);
                m_CpuMs = Mathf.Lerp(m_CpuMs, (float)m_Timing[0].cpuFrameTime, 0.1f);
            }

            HandleKeys();
        }

        void HandleKeys()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.hKey.wasPressedThisFrame) visible = !visible;

            if (keyboard.digit1Key.wasPressedThisFrame && m_Edges != null) m_Edges.SetActive(!m_Edges.isActive);
            if (keyboard.digit2Key.wasPressedThisFrame && edgeMaterial != null)
            {
                int current = Mathf.RoundToInt(edgeMaterial.GetFloat(s_TapCount));
                int index = System.Array.IndexOf(s_TapSteps, current);
                edgeMaterial.SetFloat(s_TapCount, s_TapSteps[(index + 1) % s_TapSteps.Length]);
            }
            if (keyboard.digit3Key.wasPressedThisFrame && m_Ssao != null) m_Ssao.SetActive(!m_Ssao.isActive);
            if (keyboard.digit4Key.wasPressedThisFrame && world != null) world.shadowCells = (world.shadowCells + 1) % 3;
            if (keyboard.digit5Key.wasPressedThisFrame && world != null) world.useLod = !world.useLod;
            if (keyboard.digit6Key.wasPressedThisFrame && world != null) world.frustumCulling = !world.frustumCulling;
            if (keyboard.digit7Key.wasPressedThisFrame && atmosphere != null) atmosphere.endDistance = Mathf.Max(60f, atmosphere.endDistance - 60f);
            if (keyboard.digit8Key.wasPressedThisFrame && atmosphere != null) atmosphere.endDistance += 60f;
            if (keyboard.digit0Key.wasPressedThisFrame && edgeMaterial != null)
                edgeMaterial.SetFloat(s_DebugEdges, edgeMaterial.GetFloat(s_DebugEdges) > 0.5f ? 0f : 1f);
        }

        // In the editor the profiler counter can stay at 0, so read the editor's own render stats there.
        long DrawCalls()
        {
#if UNITY_EDITOR
            return UnityEditor.UnityStats.drawCalls;
#else
            return m_DrawCalls.Valid ? m_DrawCalls.LastValue : 0;
#endif
        }

        void OnGUI()
        {
            if (!visible)
                return;

            if (m_Style == null)
                m_Style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };

            m_Text.Clear();
            if (m_GpuMs > 0f)
                m_Text.AppendFormat("<b>GPU {0:0.00} ms</b>   CPU {1:0.00} ms   (~{2:0} fps)\n", m_GpuMs, m_CpuMs, 1000f / Mathf.Max(m_GpuMs, m_CpuMs));
            else
                m_Text.AppendFormat("<b>GPU n/a</b> (enable Frame Timing Stats)   CPU {0:0.00} ms\n", m_CpuMs);
            m_Text.Append('\n');

            string On(bool value) => value ? "<color=#9f9>ON</color>" : "<color=#f99>OFF</color>";
            m_Text.AppendFormat("[1] Edges: {0}   [2] taps: {1}   [0] edge mask\n",
                On(m_Edges != null && m_Edges.isActive), edgeMaterial != null ? edgeMaterial.GetFloat(s_TapCount).ToString("0") : "-");
            m_Text.AppendFormat("[3] SSAO: {0}\n", On(m_Ssao != null && m_Ssao.isActive));
            if (world != null)
            {
                m_Text.AppendFormat("[4] Shadow cells: {0} ring(s), {1} cells cast shadows\n", world.shadowCells, world.CellsShadow);
                m_Text.AppendFormat("[5] Far LOD: {0}  ({1} far cells)\n", On(world.useLod), world.CellsFar);
                m_Text.AppendFormat("[6] Frustum culling: {0}\n", On(world.frustumCulling));
                m_Text.AppendFormat("[7/8] View distance: {0:0} m  ->  {1} cells in range, {2} drawn\n",
                    atmosphere != null ? atmosphere.endDistance : 0f, world.CellsInRange, world.CellsDrawn);
                m_Text.AppendFormat("Triangles submitted: {0:0.00} M", world.TrianglesSubmitted / 1e6f);
            }
            long drawCalls = DrawCalls();
            if (drawCalls > 0)
                m_Text.AppendFormat("   Draw calls: {0}", drawCalls);
            m_Text.Append("\n\n[F] fall   [T] tour   [G] fly (WASD, Q/E, RMB look)   [H] hide");

            const float width = 470f;
            const float height = 250f;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(10f, 10f, width, height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(20f, 16f, width - 20f, height - 30f), m_Text.ToString(), m_Style);

            // Budget bar: green below 75% of the budget, yellow up to 100%, red above.
            float fraction = m_GpuMs / budgetMs;
            Rect bar = new Rect(20f, height - 6f, width - 20f, 8f);
            GUI.color = new Color(1f, 1f, 1f, 0.2f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = fraction < 0.75f ? new Color(0.4f, 0.9f, 0.4f) : fraction < 1f ? new Color(1f, 0.85f, 0.3f) : new Color(1f, 0.4f, 0.4f);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(fraction), bar.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
