using Rayforge.Core.Diagnostics;
using Rayforge.Core.Rendering.Collections.Helpers;
using Rayforge.Core.Rendering.Helpers;
using Rayforge.Core.Rendering.Passes;
using Rayforge.Core.Utility.RenderGraphs.Collections;
using Rayforge.Core.Utility.RenderGraphs.Helpers;
using Rayforge.Core.Utility.RenderGraphs.Rendering;
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using static UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils;

namespace Rayforge.URP.Utility.RendererFeatures.DepthPyramid
{
    public class DepthPyramidPass : ScriptableRenderPass, IDisposable
    {
        public const int MipCountMax = 16;
        private const int k_DownsampleMipCountMax = MipCountMax - 1;

        private class DepthPyramidPassData : ComputePassData<DepthPyramidPassData>
        {
            public Vector2 sourceRes;
            public Vector2 destRes;

            public override void CopyUserData(DepthPyramidPassData other)
            {
                sourceRes = other.sourceRes;
                destRes = other.destRes;
            }
        }

        private const string k_DownsampleHighZKernelName = "DownsampleHighZ";

        private static readonly int k_SourceId = Shader.PropertyToID("_Source");
        private static readonly int k_DestId = Shader.PropertyToID("_Dest");

        private static readonly int k_SourceResId = Shader.PropertyToID("_SourceRes");
        private static readonly int k_DestResId = Shader.PropertyToID("_DestRes");

        private Vector2Int m_LastResolution = new Vector2Int(-1, -1);
        private int m_DownsampleMipCount = 2;
        public int MipCount
        {
            get => m_DownsampleMipCount + 1;
            set => m_DownsampleMipCount = value - 1;
        }

        private readonly RTHandleMipChain k_DepthPyramidHandles;
        private RenderTextureDescriptor m_DepthPyramidDescriptor;

        private const string k_DepthTextureMipName = "";

        private DepthPyramidPassData m_PassData = new DepthPyramidPassData();
        private ComputePassMeta m_PassMeta;

        public DepthPyramidPass(ComputeShader shader)
        {
            Assertions.NotNull(shader);

            m_PassMeta = new ComputePassMeta(shader, k_DownsampleHighZKernelName);
            if (m_PassMeta.KernelIndex < 0)
                return;
            m_PassData = new DepthPyramidPassData();

            m_DepthPyramidDescriptor = DefaultDescriptors.DepthBufferFullScreen();
            k_DepthPyramidHandles = new RTHandleMipChain((
                ref RTHandle handle,
                RenderTextureDescriptor desc,
                int mip) =>
            {
                return RenderingUtils.ReAllocateHandleIfNeeded(ref handle, desc);
            });
        }

        public void Dispose()
        {

        }

        public void UpdateMipCount(int mipCount)
        {
            m_DownsampleMipCount = Math.Clamp(mipCount - 1, 0, MipCountMax);
        }

#if UNITY_EDITOR
        private bool debug = false;
        private int debugMipLevel = 0;
        private int debugDownsampleMipLevel => debugMipLevel - 1;

        public void UpdateDebugSettings(bool showPyramid, int mipLevel)
        {
            debug = showPyramid;
            debugMipLevel = Mathf.Clamp(mipLevel, 0, k_DownsampleMipCountMax);
        }
#endif

        private void CheckAndUpdateTextures(Vector2Int baseRes)
        {
            var resolution = MipChainHelpers.DefaultMipResolution(1, baseRes);

            if (m_LastResolution != resolution)
            {
                m_DepthPyramidDescriptor.width = resolution.x;
                m_DepthPyramidDescriptor.height = resolution.y;

                m_DepthPyramidDescriptor.colorFormat = RenderTextureFormat.RFloat;
                m_DepthPyramidDescriptor.depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;

                m_LastResolution = resolution;
            }
            
            if (k_DepthPyramidHandles.MipCount != m_DownsampleMipCount)
            {
                if (m_DownsampleMipCount > 0)
                {
                    k_DepthPyramidHandles.Create(m_DepthPyramidDescriptor, m_DownsampleMipCount);
                }
                else
                {
                    k_DepthPyramidHandles.Resize(m_DownsampleMipCount);
                }
            }
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            TextureHandle srcDepthBuffer = resourceData.activeDepthTexture;

            // The following line ensures that the render pass doesn't blit from the back buffer and the color texture attachment is valid
            if (resourceData.isActiveTargetBackBuffer || !srcDepthBuffer.IsValid())
            {
                return;
            }

            var camera = cameraData.camera;
            var baseRes = new Vector2Int { x = camera.pixelWidth, y = camera.pixelHeight }; ;

            CheckAndUpdateTextures(baseRes);

            TextureHandle mipN0 = default;
            TextureHandle mipN1 = srcDepthBuffer;
            for(int i = 0; i < k_DepthPyramidHandles.MipCount; ++i)
            {
                mipN0 = mipN1;
                mipN1 = k_DepthPyramidHandles[i].ToRenderGraphHandle(renderGraph);

                if (!mipN1.IsValid())
                    break;

                Vector2Int resMipN0 = MipChainHelpers.DefaultMipResolution(i, baseRes);
                Vector2Int resMipN1 = MipChainHelpers.DefaultMipResolution(i + 1, baseRes);

                var passMeta = m_PassMeta;
                passMeta.ThreadGroupsX = Mathf.CeilToInt(resMipN1.x / 8.0f);
                passMeta.ThreadGroupsY = Mathf.CeilToInt(resMipN1.y / 8.0f);

                m_PassData.SetInput(mipN0, k_SourceId);
                m_PassData.SetDestination(mipN1, k_DestId);
                m_PassData.sourceRes = resMipN0;
                m_PassData.destRes = resMipN1;
                m_PassData.PassMeta = passMeta;
                m_PassData.UpdateCallback = static (cmd, data) =>
                {
                    var shader = data.PassMeta.Shader;
                    cmd.SetComputeVectorParam(shader, k_SourceResId, data.sourceRes);
                    cmd.SetComputeVectorParam(shader, k_DestResId, data.destRes);
                };

                RenderPassRecorder.AddComputePass(renderGraph, k_DownsampleHighZKernelName, m_PassData);
            }

#if UNITY_EDITOR
            if (debug)
            {
                TextureHandle debugHandle = default;
                if (debugDownsampleMipLevel < 0)
                {
                    debugHandle = srcDepthBuffer;
                }
                else
                {
                    debugHandle = k_DepthPyramidHandles[debugDownsampleMipLevel].ToRenderGraphHandle(renderGraph);
                }
                     
                renderGraph.AddBlitPass(debugHandle, resourceData.activeColorTexture, Vector2.one, Vector2.zero);
                return;
            }
#endif
        }
    }
}