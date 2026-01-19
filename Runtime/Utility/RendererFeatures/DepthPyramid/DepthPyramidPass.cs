using Rayforge.Core.Common;
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

namespace Rayforge.URP.Utility.RendererFeatures.DepthPyramid
{
    /// <summary>
    /// RenderPass that generates a depth pyramid from the camera's depth buffer.
    /// Supports mipmap generation and setting shader globals for each mip.
    /// </summary>
    public class DepthPyramidPass : ScriptableRenderPass, IDisposable
    {
        /// <summary>
        /// Data passed to the compute shader for each mip level.
        /// </summary>
        private class DepthPyramidPassData : ComputePassData<DepthPyramidPassData>
        {
            /// <summary>Resolution of the input mip.</summary>
            public Vector2 sourceRes;

            /// <summary>Resolution of the destination mip.</summary>
            public Vector2 destRes;

            /// <summary>Copies user data from another pass data instance.</summary>
            /// <param name="other">The source pass data to copy from.</param>
            public override void CopyUserData(DepthPyramidPassData other)
            {
                sourceRes = other.sourceRes;
                destRes = other.destRes;
            }
        }

        /// <summary>
        /// Data passed to render ggraph backend to set depthbuffer as global texture under an alternative name.
        /// </summary>
        private class DepthMip0PassData
        {
            /// <summary>Texel size of the input mip.</summary>
            public Vector4 texelSize;

            /// <summary>Shader property IDs for the depth buffer / mip 0.</summary>
            public TextureIds shaderIDs;

            /// <summary>Texture handle for the depth buffer / mip 0.</summary>
            public TextureHandle handle;
        }

        private const string kKernelName = "DownsampleHighZ";
        private static readonly int kSourceId = Shader.PropertyToID("_Source");
        private static readonly int kDestId = Shader.PropertyToID("_Dest");
        private static readonly int kSourceResId = Shader.PropertyToID("_SourceRes");
        private static readonly int kDestResId = Shader.PropertyToID("_DestRes");

        private Vector2Int m_LastResolution = new Vector2Int(-1, -1);

        private readonly RTHandleMipChain m_DepthPyramidHandles;
        private RenderTextureDescriptor m_DepthPyramidDescriptor;
        private Vector4[] m_TexelSizes = Array.Empty<Vector4>();
        private DepthPyramidPassData m_PassData = new DepthPyramidPassData();
        private ComputePassMeta m_PassMeta;

#if UNITY_EDITOR
        private bool m_Debug = false;
        private int m_DebugMipLevel = 0;
#endif

        /// <summary>
        /// Creates a DepthPyramidPass with a specified compute shader.
        /// </summary>
        /// <param name="shader">The compute shader used for downsampling.</param>
        public DepthPyramidPass(ComputeShader shader)
        {
            Assertions.NotNull(shader);
            m_PassMeta = new ComputePassMeta(shader, kKernelName);

            m_DepthPyramidDescriptor = DefaultDescriptors.DepthBufferFullScreen();
            m_DepthPyramidHandles = new RTHandleMipChain((ref RTHandle handle, RenderTextureDescriptor desc, int mip) =>
                RenderingUtils.ReAllocateHandleIfNeeded(ref handle, desc, FilterMode.Point, TextureWrapMode.Clamp)
            );
        }

        /// <summary>Disposes any allocated resources.</summary>
        public void Dispose() { }

#if UNITY_EDITOR
        /// <summary>Updates editor debug visualization settings.</summary>
        /// <param name="show">Whether to display the depth pyramid.</param>
        /// <param name="mipLevel">Mip level to visualize.</param>
        internal void UpdateDebugSettings(bool show, int mipLevel)
        {
            m_Debug = show;
            m_DebugMipLevel = Mathf.Clamp(mipLevel, 0, DepthPyramidProvider.MipCountMax - 1);
        }
#endif

        /// <summary>
        /// Checks if the RTHandles need to be resized or recreated based on camera resolution or mip count.
        /// Generates shader IDs for all mips if required.
        /// </summary>
        /// <param name="baseRes">Base resolution of the camera.</param>
        /// <returns>
        /// True if the resolution or mip count has changed and the RTHandles were updated, false otherwise.
        /// </returns>
        private bool CheckAndUpdateTextures(Vector2Int baseRes)
        {
            var resolution = MipChainHelpers.DefaultMipResolution(1, baseRes);

            bool changed = false;

            if (m_LastResolution != resolution)
            {
                m_DepthPyramidDescriptor.width = resolution.x;
                m_DepthPyramidDescriptor.height = resolution.y;
                m_DepthPyramidDescriptor.colorFormat = RenderTextureFormat.RFloat;
                m_DepthPyramidDescriptor.depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
                m_LastResolution = resolution;

                changed = true;
            }

            var downsampleMipCount = Mathf.Clamp(DepthPyramidProvider.MipCount - 1, 0, DepthPyramidProvider.MipCountMax - 1);
            if (m_DepthPyramidHandles.MipCount != downsampleMipCount)
            {
                if (downsampleMipCount > 0)
                    m_DepthPyramidHandles.Create(m_DepthPyramidDescriptor, downsampleMipCount);
                else
                    m_DepthPyramidHandles.Resize(0);

                changed = true;
            }

            if (changed)
            {
                DepthPyramidProvider.Generate(baseRes);
            }

            DepthPyramidProvider.ResetMipCountDirty();
            return changed;
        }

        /// <summary>
        /// Binds all Depth Pyramid mip textures and texel sizes as global shader variables.
        /// <para>
        /// Uses the cached DepthPyramidProvider for IDs and texel sizes, and RTHandleMipChain for the actual textures.
        /// Mip 0 will be set from the source depth buffer; higher mips from the chain.
        /// </para>
        /// </summary>
        /// <param name="depthPyramidHandles">The RTHandleMipChain containing the depth pyramid mips (excluding mip 0).</param>
        /// <param name="sourceDepth">The original camera depth buffer (mip 0).</param>
        private static void SetGlobalDepthPyramid(RTHandleMipChain depthPyramidHandles, RTHandle sourceDepth = null)
        {
            if (depthPyramidHandles == null) throw new ArgumentNullException(nameof(depthPyramidHandles));

            for (int i = 0; i < DepthPyramidProvider.ActiveMipCount; ++i)
            {
                var mip = DepthPyramidProvider.GetMip(i);

                Shader.SetGlobalVector(mip.Ids.texelSize, mip.TexelSize);

                RTHandle handle = i == 0 ? sourceDepth : depthPyramidHandles[i - 1];
                if (handle != null)
                {
                    Shader.SetGlobalTexture(mip.Ids.texture, handle);
                }
            }
        }

        /// <summary>
        /// Records the depth pyramid pass into the RenderGraph.
        /// Sets shader globals for each mip level.
        /// </summary>
        /// <param name="renderGraph">The render graph to record into.</param>
        /// <param name="frameData">Frame-specific context data.</param>
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            var srcDepthBuffer = resourceData.activeDepthTexture;
            if (resourceData.isActiveTargetBackBuffer || !srcDepthBuffer.IsValid())
                return;

            var camera = cameraData.camera;
            var baseRes = new Vector2Int(camera.pixelWidth, camera.pixelHeight);
            if (CheckAndUpdateTextures(baseRes))
            {
                SetGlobalDepthPyramid(m_DepthPyramidHandles);
            }

            TextureHandle prevMip = srcDepthBuffer;
            Vector2 prevRes = baseRes;

            for (int i = 0; i < m_DepthPyramidHandles.MipCount; ++i)
            {
                var curMip = m_DepthPyramidHandles[i].ToRenderGraphHandle(renderGraph);
                if (!curMip.IsValid()) break;

                var mipData = DepthPyramidProvider.GetMip(i + 1);

                Vector4 texelSize = mipData.TexelSize;
                Vector2 curRes = new Vector2(texelSize.z, texelSize.w);

                var passMeta = m_PassMeta;
                passMeta.ThreadGroupsX = Mathf.CeilToInt(curRes.x / 8.0f);
                passMeta.ThreadGroupsY = Mathf.CeilToInt(curRes.y / 8.0f);

                m_PassData.PushInput(prevMip, kSourceId);
                m_PassData.PushDestination(curMip, kDestId);
                m_PassData.sourceRes = prevRes;
                m_PassData.destRes = curRes;
                m_PassData.PassMeta = passMeta;
                m_PassData.RenderFuncUpdate = static (cmd, data) =>
                {
                    cmd.SetComputeVectorParam(data.PassMeta.Shader, kSourceResId, data.sourceRes);
                    cmd.SetComputeVectorParam(data.PassMeta.Shader, kDestResId, data.destRes);
                };

                RenderPassRecorder.AddComputePass(renderGraph, kKernelName, m_PassData);
                prevMip = curMip;
                prevRes = curRes;
            }

#if UNITY_EDITOR
            if (m_Debug)
            {
                var downsampleMipLevel = m_DebugMipLevel - 1;
                TextureHandle debugHandle = downsampleMipLevel < 0 ? srcDepthBuffer
                    : m_DepthPyramidHandles[downsampleMipLevel].ToRenderGraphHandle(renderGraph);

                renderGraph.AddBlitPass(debugHandle, resourceData.activeColorTexture, Vector2.one, Vector2.zero);
            }
#endif
        }
    }
}
