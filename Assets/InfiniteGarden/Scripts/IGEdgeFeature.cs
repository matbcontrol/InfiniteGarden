using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace InfiniteGarden
{
    /// <summary>
    /// Manifold Garden style outlines as a post pass. Compares depth and normals of neighbouring pixels
    /// and draws anti-aliased lines from the distance to the nearest discontinuity (see IG_Edges.shader).
    /// </summary>
    public class IGEdgeFeature : ScriptableRendererFeature
    {
        public Material material;
        public RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingPostProcessing;

        EdgePass m_Pass;

        public override void Create()
        {
            m_Pass = new EdgePass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (material == null)
                return;

            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
                return;

            m_Pass.Setup(material);
            m_Pass.renderPassEvent = injectionPoint;
            // Ask URP for the depth and normals textures (it adds a DepthNormals prepass if needed).
            m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
            // We read the camera color, so it must be an intermediate texture, not the back buffer.
            m_Pass.requiresIntermediateTexture = true;
            renderer.EnqueuePass(m_Pass);
        }

        class EdgePass : ScriptableRenderPass
        {
            Material m_Material;

            class PassData
            {
                public Material material;
                public TextureHandle source;
            }

            public EdgePass()
            {
                profilingSampler = new ProfilingSampler("IG Edges");
            }

            public void Setup(Material material) => m_Material = material;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer)
                    return;

                // Read the current camera color, write the outlined image into a new texture,
                // then make that texture the camera color. One pass, no extra copy back.
                TextureHandle source = resources.activeColorTexture;
                TextureDesc desc = renderGraph.GetTextureDesc(source);
                desc.name = "_IG_EdgesColor";
                desc.clearBuffer = false;
                TextureHandle destination = renderGraph.CreateTexture(desc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var data, profilingSampler))
                {
                    data.material = m_Material;
                    data.source = source;

                    builder.UseTexture(source);
                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture);
                    if (resources.cameraNormalsTexture.IsValid())
                        builder.UseTexture(resources.cameraNormalsTexture);
                    builder.SetRenderAttachment(destination, 0);

                    builder.SetRenderFunc(static (PassData d, RasterGraphContext context) =>
                    {
                        Blitter.BlitTexture(context.cmd, d.source, new Vector4(1f, 1f, 0f, 0f), d.material, 0);
                    });
                }

                resources.cameraColor = destination;
            }
        }
    }
}
