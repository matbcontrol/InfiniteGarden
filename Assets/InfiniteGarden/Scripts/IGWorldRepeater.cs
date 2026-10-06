using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace InfiniteGarden
{
    /// <summary>
    /// Infinite world, Manifold Garden style. All child meshes (one "tile" of the level) are merged
    /// into one mesh, and that mesh is drawn again in a 3D grid of cells around each camera with GPU instancing.
    /// The fog makes everything past IGAtmosphere.endDistance invisible, so that distance decides
    /// how many copies we have to pay for.
    /// Keep this object at (0,0,0) with no rotation and scale 1.
    /// </summary>
    public class IGWorldRepeater : MonoBehaviour
    {
        [Header("Level")]
        [Tooltip("Size of one repeating cell (m). The camera wraps around at its borders.")]
        public float tileSize = 48f;
        [Tooltip("Objects whose largest side is smaller than this (m) are left out of the far mesh.")]
        public float detailSize = 2.5f;
        public Material material;

        [Header("Budget knobs")]
        [Tooltip("Cells up to this many steps from the camera cast shadows. 0 = only the camera's own cell.")]
        [Range(0, 3)] public int shadowCells = 1;
        [Tooltip("Cells farther than this (m) use the far mesh without small details.")]
        public float lodDistance = 100f;
        public bool useLod = true;
        public bool frustumCulling = true;

        public Mesh NearMesh { get; private set; }
        public Mesh FarMesh { get; private set; }

        // Stats of the last game-camera frame (shown by IGBudgetHUD).
        public int CellsInRange { get; private set; }
        public int CellsDrawn { get; private set; }
        public int CellsFar { get; private set; }
        public int CellsShadow { get; private set; }
        public long TrianglesSubmitted { get; private set; }

        readonly List<Matrix4x4> m_Shadowed = new List<Matrix4x4>();
        readonly List<Matrix4x4> m_Near = new List<Matrix4x4>();
        readonly List<Matrix4x4> m_Far = new List<Matrix4x4>();
        readonly Plane[] m_Planes = new Plane[6];
        long m_NearTriangles, m_FarTriangles;

        void Start() => Rebuild();

        void OnEnable() => RenderPipelineManager.beginCameraRendering += OnBeginCamera;

        void OnDisable() => RenderPipelineManager.beginCameraRendering -= OnBeginCamera;

        void OnBeginCamera(ScriptableRenderContext context, Camera camera) => Submit(camera);

        /// <summary>Merges the child meshes into the near and far tile meshes and hides the originals.</summary>
        public void Rebuild()
        {
            var near = new List<CombineInstance>();
            var far = new List<CombineInstance>();
            Matrix4x4 worldToLocal = transform.worldToLocalMatrix;

            foreach (MeshFilter filter in GetComponentsInChildren<MeshFilter>())
            {
                var meshRenderer = filter.GetComponent<MeshRenderer>();
                if (filter.sharedMesh == null || meshRenderer == null)
                    continue;

                var part = new CombineInstance
                {
                    mesh = filter.sharedMesh,
                    transform = worldToLocal * filter.transform.localToWorldMatrix
                };
                near.Add(part);

                Vector3 size = meshRenderer.bounds.size;
                if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) >= detailSize)
                    far.Add(part);

                // The repeater draws every copy, including the centre one.
                meshRenderer.enabled = false;
            }

            NearMesh = Combine("IG Tile Near", near);
            FarMesh = Combine("IG Tile Far", far);
            m_NearTriangles = NearMesh.GetIndexCount(0) / 3;
            m_FarTriangles = FarMesh.GetIndexCount(0) / 3;

            if (material != null)
                material.enableInstancing = true;
        }

        static Mesh Combine(string name, List<CombineInstance> parts)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.CombineMeshes(parts.ToArray(), true, true);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Queues the instanced draws for one camera. Called right before that camera renders.</summary>
        public void Submit(Camera camera)
        {
            if (NearMesh == null || material == null)
                return;
            if (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)
                return;

            IGAtmosphere atmosphere = IGAtmosphere.Active;
            float viewDistance = atmosphere != null ? atmosphere.endDistance : 300f;
            int radius = Mathf.CeilToInt(viewDistance / tileSize);

            Vector3 origin = transform.position;
            Vector3 cameraPosition = camera.transform.position;
            Vector3Int cameraCell = Vector3Int.RoundToInt((cameraPosition - origin) / tileSize);
            Bounds tileBounds = NearMesh.bounds;
            GeometryUtility.CalculateFrustumPlanes(camera, m_Planes);

            m_Shadowed.Clear();
            m_Near.Clear();
            m_Far.Clear();
            int inRange = 0;

            for (int x = -radius; x <= radius; x++)
            for (int y = -radius; y <= radius; y++)
            for (int z = -radius; z <= radius; z++)
            {
                Vector3 offset = origin + (Vector3)(cameraCell + new Vector3Int(x, y, z)) * tileSize;
                var bounds = new Bounds(tileBounds.center + offset, tileBounds.size);

                // Completely inside the fog = invisible: skip.
                float distance = Mathf.Sqrt(bounds.SqrDistance(cameraPosition));
                if (distance > viewDistance)
                    continue;
                inRange++;

                // Shadow casters near the camera can throw shadows into view from outside the frustum,
                // so they are never frustum-culled.
                int ring = Mathf.Max(Mathf.Abs(x), Mathf.Max(Mathf.Abs(y), Mathf.Abs(z)));
                bool castsShadow = ring <= shadowCells;
                if (!castsShadow && frustumCulling && !GeometryUtility.TestPlanesAABB(m_Planes, bounds))
                    continue;

                Matrix4x4 matrix = Matrix4x4.Translate(offset);
                if (castsShadow)
                    m_Shadowed.Add(matrix);
                else if (useLod && distance > lodDistance)
                    m_Far.Add(matrix);
                else
                    m_Near.Add(matrix);
            }

            var worldBounds = new Bounds(cameraPosition, Vector3.one * ((2 * radius + 2) * tileSize));
            Draw(camera, NearMesh, m_Shadowed, ShadowCastingMode.On, worldBounds);
            Draw(camera, NearMesh, m_Near, ShadowCastingMode.Off, worldBounds);
            Draw(camera, FarMesh, m_Far, ShadowCastingMode.Off, worldBounds);

            if (camera.cameraType == CameraType.Game)
            {
                CellsInRange = inRange;
                CellsDrawn = m_Shadowed.Count + m_Near.Count + m_Far.Count;
                CellsFar = m_Far.Count;
                CellsShadow = m_Shadowed.Count;
                TrianglesSubmitted = (m_Shadowed.Count + m_Near.Count) * m_NearTriangles + m_Far.Count * m_FarTriangles;
            }
        }

        void Draw(Camera camera, Mesh mesh, List<Matrix4x4> matrices, ShadowCastingMode shadows, Bounds worldBounds)
        {
            if (matrices.Count == 0)
                return;

            var renderParams = new RenderParams(material)
            {
                camera = camera,
                worldBounds = worldBounds,
                shadowCastingMode = shadows,
                receiveShadows = true,
                layer = gameObject.layer
            };

            const int maxPerCall = 1023;
            for (int start = 0; start < matrices.Count; start += maxPerCall)
                Graphics.RenderMeshInstanced(renderParams, mesh, 0, matrices, Mathf.Min(maxPerCall, matrices.Count - start), start);
        }
    }
}
