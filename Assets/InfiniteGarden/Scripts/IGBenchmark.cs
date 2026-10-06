using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace InfiniteGarden
{
    /// <summary>
    /// Automatic, repeatable frame-budget benchmark. Every configuration replays the same tour path:
    /// a warm-up, then frame, GPU and CPU (main / render thread) times are sampled and written to a CSV
    /// (average, median, 95th / 99th percentile). A player started with the -benchmark argument runs it and quits.
    /// Numbers are only meaningful in a player build, not in the editor.
    /// </summary>
    public class IGBenchmark : MonoBehaviour
    {
        public IGWorldRepeater world;
        public IGAtmosphere atmosphere;
        public IGFlyCamera flyCamera;
        public IGBudgetHUD hud;
        [Tooltip("The renderer asset that holds the IG Edge and SSAO features (PC_Renderer in the URP template).")]
        public UniversalRendererData rendererData;
        public Material edgeMaterial;

        [Tooltip("Run when entering Play mode in the editor (for testing). Players run only with -benchmark.")]
        public bool runInEditor;
        [Tooltip("Throw-away run before the first configuration: shader compilation, pipeline creation, mesh merge.")]
        public float initialWarmupSeconds = 5f;
        public float warmupSeconds = 2f;
        public float measureSeconds = 6f;
        [Tooltip("Every configuration is measured this many times; even passes run in reverse order so heat-up doesn't favour one end of the list.")]
        [Min(1)] public int passes = 3;

        struct Config
        {
            public string name;
            public bool edges;
            public int taps;
            public bool ssao;
            public int shadowCells;
            public bool lod;
            public bool culling;
            public float viewDistance;
            public float renderScale;
        }

        // Samples of one configuration. Frame time comes from Time.unscaledDeltaTime;
        // GPU and per-thread CPU times come from FrameTimingManager (they arrive a few frames late).
        class Samples
        {
            public readonly List<float> frame = new List<float>(4096);
            public readonly List<float> gpu = new List<float>(4096);
            public readonly List<float> cpuMain = new List<float>(4096);
            public readonly List<float> cpuRender = new List<float>(4096);

            public void Clear()
            {
                frame.Clear();
                gpu.Clear();
                cpuMain.Clear();
                cpuRender.Clear();
            }
        }

        const float k_MaxSaneMs = 1000f; // FrameTimingManager occasionally reports garbage; drop it
        static readonly int s_TapCount = Shader.PropertyToID("_TapCount");

        readonly FrameTiming[] m_Timings = new FrameTiming[8];
        readonly StringBuilder m_Csv = new StringBuilder();
        ScriptableRendererFeature m_Edges, m_Ssao;
        Config m_Original;
        ulong m_LastTimingStamp;
        bool m_Running;
        int m_RowsWritten, m_ConfigCount;
        string m_Status = "";

        static Config Baseline(string name) => new Config
        {
            name = name, edges = true, taps = 20, ssao = true, shadowCells = 1, lod = true, culling = true, viewDistance = 300f, renderScale = 1f
        };

        // One knob changed at a time against the baseline, plus one combined low-end preset.
        static List<Config> BuildConfigs()
        {
            var configs = new List<Config> { Baseline("baseline") };
            Config c;
            c = Baseline("edges_off"); c.edges = false; configs.Add(c);
            c = Baseline("edges_4_taps"); c.taps = 4; configs.Add(c);
            c = Baseline("edges_8_taps"); c.taps = 8; configs.Add(c);
            c = Baseline("ssao_off"); c.ssao = false; configs.Add(c);
            c = Baseline("shadow_cells_0"); c.shadowCells = 0; configs.Add(c);
            c = Baseline("shadow_cells_2"); c.shadowCells = 2; configs.Add(c);
            c = Baseline("lod_off"); c.lod = false; configs.Add(c);
            c = Baseline("culling_off"); c.culling = false; configs.Add(c);
            c = Baseline("view_180m"); c.viewDistance = 180f; configs.Add(c);
            c = Baseline("view_420m"); c.viewDistance = 420f; configs.Add(c);
            c = Baseline("low_end_preset"); c.taps = 8; c.ssao = false; c.shadowCells = 0; c.viewDistance = 240f; configs.Add(c);
            // Integrated GPUs are pixel-bound (edges and SSAO are full-screen passes): render fewer pixels and upscale.
            c = Baseline("baseline_75pct_res"); c.renderScale = 0.75f; configs.Add(c);
            c = Baseline("low_end_75pct_res"); c.taps = 8; c.ssao = false; c.shadowCells = 0; c.viewDistance = 240f; c.renderScale = 0.75f; configs.Add(c);
            return configs;
        }

        void Start()
        {
            bool requested = Array.IndexOf(Environment.GetCommandLineArgs(), "-benchmark") >= 0;
            if (Application.isEditor)
                requested |= runInEditor;
            if (requested)
                StartCoroutine(Run());
        }

        // Unity does not run an iterator's finally block when Play mode stops mid-coroutine:
        // put the asset-backed settings back here instead.
        void OnDisable()
        {
            if (m_Running)
                Restore();
        }

        IEnumerator Run()
        {
            if (world == null || atmosphere == null || flyCamera == null)
            {
                Debug.LogError("IGBenchmark: assign world, atmosphere and flyCamera.");
                QuitPlayer(1);
                yield break;
            }
            if (!FrameTimingManager.IsFeatureEnabled())
                Debug.LogWarning("IGBenchmark: Frame Timing Stats is off (Player Settings), GPU/CPU columns will be n/a.");

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            FindFeatures();
            m_Original = Current();
            m_Running = true;
            if (hud != null)
                hud.enabled = false; // it samples FrameTimingManager and draws GUI every frame

            List<Config> configs = BuildConfigs();
            m_ConfigCount = configs.Count * passes;
            m_Csv.Clear();
            WriteHeader();
            bool finished = false;

            try
            {
                Apply(configs[0]);
                flyCamera.BeginTour();
                m_Status = "Benchmark: initial warm-up";
                yield return WaitRealtime(initialWarmupSeconds);

                var samples = new Samples();
                for (int run = 0; run < m_ConfigCount; run++)
                {
                    int pass = run / configs.Count + 1;
                    int i = run % configs.Count;
                    Config config = configs[pass % 2 == 0 ? configs.Count - 1 - i : i];
                    Apply(config);
                    flyCamera.BeginTour();
                    m_Status = $"Benchmark pass {pass}/{passes}, {i + 1}/{configs.Count}: {config.name}";
                    Debug.Log(m_Status);
                    yield return WaitRealtime(warmupSeconds);

                    samples.Clear();
                    SkipPendingTimings(); // timings still in flight belong to warm-up frames
                    int frames = 0;
                    double cellsSum = 0, trianglesSum = 0;
                    float start = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - start < measureSeconds)
                    {
                        yield return null;
                        frames++;
                        float frameMs = Time.unscaledDeltaTime * 1000f;
                        if (frameMs > 0f && frameMs < k_MaxSaneMs) samples.frame.Add(frameMs);
                        cellsSum += world.CellsDrawn;
                        trianglesSum += world.TrianglesSubmitted;
                        CollectTimings(samples);
                    }

                    float elapsed = Time.realtimeSinceStartup - start;
                    AppendRow(pass, config.name, frames / elapsed, samples, cellsSum / frames, trianglesSum / frames / 1e6);
                }
                finished = true;
            }
            finally
            {
                Finish(finished);
            }
        }

        static IEnumerator WaitRealtime(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
                yield return null;
        }

        void SkipPendingTimings()
        {
            FrameTimingManager.CaptureFrameTimings();
            uint count = FrameTimingManager.GetLatestTimings((uint)m_Timings.Length, m_Timings);
            for (int i = 0; i < count; i++)
                m_LastTimingStamp = Math.Max(m_LastTimingStamp, m_Timings[i].frameStartTimestamp);
        }

        // Takes every newly completed frame exactly once (de-duplicated by its start timestamp).
        void CollectTimings(Samples samples)
        {
            FrameTimingManager.CaptureFrameTimings();
            uint count = FrameTimingManager.GetLatestTimings((uint)m_Timings.Length, m_Timings);
            ulong newest = m_LastTimingStamp;
            for (int i = 0; i < count; i++)
            {
                FrameTiming t = m_Timings[i];
                if (t.frameStartTimestamp <= m_LastTimingStamp)
                    continue;
                newest = Math.Max(newest, t.frameStartTimestamp);
                AddSane(samples.gpu, t.gpuFrameTime);
                AddSane(samples.cpuMain, t.cpuMainThreadFrameTime);
                AddSane(samples.cpuRender, t.cpuRenderThreadFrameTime);
            }
            m_LastTimingStamp = newest;
        }

        static void AddSane(List<float> list, double ms)
        {
            if (ms > 0.0 && ms < k_MaxSaneMs)
                list.Add((float)ms);
        }

        void Finish(bool finished)
        {
            Restore();
            if (!finished)
                m_Csv.AppendLine($"# status,incomplete ({m_RowsWritten}/{m_ConfigCount} configurations)");

            string path = WriteCsv();
            m_Status = (finished ? "Benchmark done: " : "Benchmark stopped early: ") + path;
            Debug.Log(m_Status);
            QuitPlayer(finished ? 0 : 1);
        }

        void Restore()
        {
            Apply(m_Original);
            if (hud != null)
                hud.enabled = true;
            m_Running = false;
        }

        static void QuitPlayer(int exitCode)
        {
            if (!Application.isEditor)
                Application.Quit(exitCode);
        }

        void FindFeatures()
        {
            if (rendererData == null)
                return;
            foreach (ScriptableRendererFeature feature in rendererData.rendererFeatures)
            {
                if (feature is IGEdgeFeature) m_Edges = feature;
                else if (feature is ScreenSpaceAmbientOcclusion) m_Ssao = feature;
            }
        }

        Config Current() => new Config
        {
            name = "original",
            edges = m_Edges == null || m_Edges.isActive,
            taps = edgeMaterial != null ? Mathf.RoundToInt(edgeMaterial.GetFloat(s_TapCount)) : 20,
            ssao = m_Ssao == null || m_Ssao.isActive,
            shadowCells = world.shadowCells,
            lod = world.useLod,
            culling = world.frustumCulling,
            viewDistance = atmosphere.endDistance,
            renderScale = UniversalRenderPipeline.asset != null ? UniversalRenderPipeline.asset.renderScale : 1f
        };

        void Apply(Config c)
        {
            if (m_Edges != null) m_Edges.SetActive(c.edges);
            if (m_Ssao != null) m_Ssao.SetActive(c.ssao);
            if (edgeMaterial != null) edgeMaterial.SetFloat(s_TapCount, c.taps);
            world.shadowCells = c.shadowCells;
            world.useLod = c.lod;
            world.frustumCulling = c.culling;
            atmosphere.endDistance = c.viewDistance;
            if (UniversalRenderPipeline.asset != null)
                UniversalRenderPipeline.asset.renderScale = c.renderScale;
        }

        static string Clean(string value) => value.Replace(',', ' ').Trim();

        void WriteHeader()
        {
            m_Csv.AppendLine("# Infinite Garden benchmark");
            m_Csv.AppendLine("# mode," + (Application.isEditor ? "editor (not representative)" : "player"));
            m_Csv.AppendLine("# gpu," + Clean(SystemInfo.graphicsDeviceName));
            m_Csv.AppendLine("# graphics_api," + SystemInfo.graphicsDeviceType);
            m_Csv.AppendLine("# cpu," + Clean(SystemInfo.processorType));
            m_Csv.AppendLine($"# resolution,{Screen.width}x{Screen.height}");
            m_Csv.AppendLine("# unity," + Application.unityVersion);
            m_Csv.AppendLine("# date," + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            m_Csv.AppendLine("# warmup_s," + F(warmupSeconds) + ",measure_s," + F(measureSeconds) + ",initial_warmup_s," + F(initialWarmupSeconds));
            m_Csv.AppendLine("# passes," + passes);
            m_Csv.AppendLine("pass,config,fps,frame_avg_ms,frame_p95_ms,frame_p99_ms,gpu_avg_ms,gpu_median_ms,gpu_p95_ms," +
                             "cpu_main_avg_ms,cpu_main_p95_ms,cpu_render_avg_ms,cells_drawn,triangles_m,gpu_samples");
        }

        void AppendRow(int pass, string name, float fps, Samples s, double cells, double trianglesM)
        {
            s.frame.Sort();
            s.gpu.Sort();
            s.cpuMain.Sort();
            s.cpuRender.Sort();
            m_Csv.Append(pass).Append(',').Append(name)
                .Append(',').Append(F(fps))
                .Append(',').Append(Avg(s.frame)).Append(',').Append(Pct(s.frame, 0.95)).Append(',').Append(Pct(s.frame, 0.99))
                .Append(',').Append(Avg(s.gpu)).Append(',').Append(Pct(s.gpu, 0.5)).Append(',').Append(Pct(s.gpu, 0.95))
                .Append(',').Append(Avg(s.cpuMain)).Append(',').Append(Pct(s.cpuMain, 0.95))
                .Append(',').Append(Avg(s.cpuRender))
                .Append(',').Append(F(cells)).Append(',').Append(F(trianglesM))
                .Append(',').Append(s.gpu.Count)
                .AppendLine();
            m_RowsWritten++;
        }

        static string Avg(List<float> values)
        {
            if (values.Count == 0) return "n/a";
            double sum = 0;
            foreach (float v in values) sum += v;
            return F(sum / values.Count);
        }

        // Nearest-rank percentile of an already sorted list.
        static string Pct(List<float> sorted, double p)
        {
            if (sorted.Count == 0) return "n/a";
            int index = Mathf.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
            return F(sorted[index]);
        }

        // Invariant culture: a comma decimal separator would break the CSV on non-English systems.
        static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        string WriteCsv()
        {
            string gpuName = Clean(SystemInfo.graphicsDeviceName).Replace(' ', '_');
            foreach (char invalid in Path.GetInvalidFileNameChars())
                gpuName = gpuName.Replace(invalid, '_');
            string file = $"benchmark_{gpuName}_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.csv";

            // Players write next to the .exe; the editor writes into Logs/ (ignored by git).
            string root = Path.GetDirectoryName(Application.dataPath);
            string folder = Application.isEditor ? Path.Combine(root, "Logs") : root;
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, file);
                File.WriteAllText(path, m_Csv.ToString());
                return path;
            }
            catch (Exception e)
            {
                // Read-only install folder: fall back to the per-user data folder.
                Debug.LogWarning("IGBenchmark: could not write next to the executable (" + e.Message + ")");
                string path = Path.Combine(Application.persistentDataPath, file);
                File.WriteAllText(path, m_Csv.ToString());
                return path;
            }
        }

        void OnGUI()
        {
            if (!string.IsNullOrEmpty(m_Status))
                GUI.Label(new Rect(10f, Screen.height - 30f, 900f, 24f), m_Status);
        }
    }
}
