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
        /// Maximum number of mip levels supported.
        /// </summary>
        public const int MipCountMax = 16;

        /// <summary>
        /// Data passed to the compute shader for each mip level.
        /// </summary>
        private class DepthPyramidPassData : ComputePassData<DepthPyramidPassData>
        {
            /// <summary>Resolution of the input mip.</summary>
            public Vector2 sourceRes;

            /// <summary>Resolution of the destination mip.</summary>
            public Vector2 destRes;

            /// <summary>Shader property IDs for the current mip.</summary>
            public TextureIds shaderIDs;

            /// <summary>Copies user data from another pass data instance.</summary>
            /// <param name="other">The source pass data to copy from.</param>
            public override void CopyUserData(DepthPyramidPassData other)
            {
                sourceRes = other.sourceRes;
                destRes = other.destRes;
                shaderIDs = other.shaderIDs;
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
        private int m_DownsampleMipCount = 2;

        /// <summary>
        /// Gets or sets the number of mip levels for the pyramid (including mip 0).
        /// Setting clamps the value between 1 and MipCountMax.
        /// </summary>
        private int MipCount
        {
            get => m_DownsampleMipCount + 1;
            set => m_DownsampleMipCount = Mathf.Clamp(value - 1, 0, MipCountMax - 1);
        }

        private readonly RTHandleMipChain m_DepthPyramidHandles;
        private RenderTextureDescriptor m_DepthPyramidDescriptor;
        private DepthPyramidPassData m_PassData = new DepthPyramidPassData();
        private ComputePassMeta m_PassMeta;

#if UNITY_EDITOR
        private bool m_Debug = false;
        private int m_DebugMipLevel = 0;

        /// <summary>Editor-only debug mip level, for visualizing the depth pyramid.</summary>
        private int DebugDownsampleMipLevel => m_DebugMipLevel - 1;
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

        /// <summary>Updates the mip count at runtime.</summary>
        /// <param name="mipCount">Number of mip levels including mip 0.</param>
        internal void UpdateMipCount(int mipCount) => MipCount = mipCount;

#if UNITY_EDITOR
        /// <summary>Updates editor debug visualization settings.</summary>
        /// <param name="show">Whether to display the depth pyramid.</param>
        /// <param name="mipLevel">Mip level to visualize.</param>
        public void UpdateDebugSettings(bool show, int mipLevel)
        {
            m_Debug = show;
            m_DebugMipLevel = Mathf.Clamp(mipLevel, 0, MipCountMax - 1);
        }
#endif

        /// <summary>
        /// Checks if the RTHandles need to be resized or recreated based on camera resolution or mip count.
        /// Generates shader IDs for all mips if required.
        /// </summary>
        /// <param name="baseRes">Base resolution of the camera.</param>
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

            if (m_DepthPyramidHandles.MipCount != m_DownsampleMipCount)
            {
                if (m_DownsampleMipCount > 0)
                    m_DepthPyramidHandles.Create(m_DepthPyramidDescriptor, m_DownsampleMipCount);
                else
                    m_DepthPyramidHandles.Resize(m_DownsampleMipCount);

                DepthPyramidGlobals.Generate(MipCount);
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
            CheckAndUpdateTextures(baseRes);

            // expose depth buffer as mip 0, just for convenience. Basically the same as _CameraDepthTexture.
            if (DepthPyramidGlobals.Ids.Length > 0)
            {
                using(var builder = renderGraph.AddUnsafePass("depth mip 0 pass", out DepthMip0PassData data))
                {
                    var mip0Ids = DepthPyramidGlobals.GetMipIds(0);
                    Vector4 texelSize0 = new Vector4(1f / baseRes.x, 1f / baseRes.y, baseRes.x, baseRes.y);

                    data.texelSize = texelSize0;
                    data.shaderIDs = mip0Ids;
                    data.handle = srcDepthBuffer;

                    builder.SetRenderFunc(static (DepthMip0PassData data, UnsafeGraphContext ctx) =>
                    {
                        ctx.cmd.SetGlobalTexture(data.shaderIDs.texture, data.handle);
                        ctx.cmd.SetGlobalVector(data.shaderIDs.texelSize, data.texelSize);
                    });
                }
            }

            TextureHandle prevMip = srcDepthBuffer;

            for (int i = 0; i < m_DepthPyramidHandles.MipCount; ++i)
            {
                var curMip = m_DepthPyramidHandles[i].ToRenderGraphHandle(renderGraph);
                if (!curMip.IsValid()) break;

                Vector2Int prevRes = MipChainHelpers.DefaultMipResolution(i, baseRes);
                Vector2Int curRes = MipChainHelpers.DefaultMipResolution(i + 1, baseRes);

                m_PassData.SetInput(prevMip, kSourceId);
                m_PassData.SetDestination(curMip, kDestId);
                m_PassData.sourceRes = prevRes;
                m_PassData.destRes = curRes;
                m_PassData.shaderIDs = DepthPyramidGlobals.Ids[i + 1];
                m_PassData.PassMeta = m_PassMeta;
                m_PassData.UpdateCallback = static (cmd, data) =>
                {
                    cmd.SetComputeVectorParam(data.PassMeta.Shader, kSourceResId, data.sourceRes);
                    cmd.SetComputeVectorParam(data.PassMeta.Shader, kDestResId, data.destRes);

                    Vector4 texelSize = new Vector4(1.0f / data.destRes.x, 1.0f / data.destRes.y, data.destRes.x, data.destRes.y);
                    cmd.SetGlobalTexture(data.shaderIDs.texture, data.Destination.handle);
                    cmd.SetGlobalVector(data.shaderIDs.texelSize, texelSize);
                };

                RenderPassRecorder.AddComputePass(renderGraph, kKernelName, m_PassData);
                prevMip = curMip;
            }

#if UNITY_EDITOR
            if (m_Debug)
            {
                TextureHandle debugHandle = DebugDownsampleMipLevel < 0 ? srcDepthBuffer
                    : m_DepthPyramidHandles[DebugDownsampleMipLevel].ToRenderGraphHandle(renderGraph);

                renderGraph.AddBlitPass(debugHandle, resourceData.activeColorTexture, Vector2.one, Vector2.zero);
            }
#endif
        }
    }
}
