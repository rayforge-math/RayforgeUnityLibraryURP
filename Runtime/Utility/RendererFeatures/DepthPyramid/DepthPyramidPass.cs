using Mono.Cecil.Cil;
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
            bool changed = false;

            if (m_LastResolution != baseRes)
            {
                m_DepthPyramidDescriptor.width = baseRes.x;
                m_DepthPyramidDescriptor.height = baseRes.y;
                m_LastResolution = baseRes;

                changed = true;
            }

            var mipCount = Mathf.Clamp(DepthPyramidProvider.MipCount, 0, DepthPyramidProvider.MipCountMax);
            if (m_DepthPyramidHandles.MipCount != mipCount)
            {
                if (mipCount > 0)
                    m_DepthPyramidHandles.Create(m_DepthPyramidDescriptor, mipCount);
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
        /// Records the depth pyramid pass into the RenderGraph.
        /// Sets shader globals for each mip level.
        /// </summary>
        /// <param name="renderGraph">The render graph to record into.</param>
        /// <param name="frameData">Frame-specific context data.</param>
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            var depthData = frameData.GetOrCreate<DepthPyramidFrameData>();

            var srcDepthBuffer = resourceData.activeDepthTexture;
            if (resourceData.isActiveTargetBackBuffer || !srcDepthBuffer.IsValid())
                return;

            var camera = cameraData.camera;
            var baseRes = new Vector2Int(camera.pixelWidth, camera.pixelHeight);
            if (CheckAndUpdateTextures(baseRes))
            {
                DepthPyramidProvider.SetGlobalDepthPyramid(m_DepthPyramidHandles);
            }

            var firstMip = m_DepthPyramidHandles[0].ToRenderGraphHandle(renderGraph);
            renderGraph.AddBlitPass(srcDepthBuffer, firstMip, Vector2.one, Vector2.zero);

            depthData.mips[0] = new TextureHandleMeta<TextureHandle>
            {
                Handle = firstMip,
                Meta = DepthPyramidProvider.GetMip(0).Meta
            };

            TextureHandle prevMip = firstMip;
            Vector2 prevRes = baseRes;

            for (int i = 1; i < m_DepthPyramidHandles.MipCount; ++i)
            {
                var curMip = m_DepthPyramidHandles[i].ToRenderGraphHandle(renderGraph);
                if (!curMip.IsValid()) break;

                var mipData = DepthPyramidProvider.GetMip(i);

                Vector4 texelSize = mipData.Meta.TexelSize;
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

                depthData.mips[i] = new TextureHandleMeta<TextureHandle>
                {
                    Handle = curMip,
                    Meta = DepthPyramidProvider.GetMip(i).Meta
                };
            }

#if UNITY_EDITOR
            if (m_Debug)
            {
                TextureHandle debugHandle = depthData.mips[m_DebugMipLevel].Handle;
                renderGraph.AddBlitPass(debugHandle, resourceData.activeColorTexture, Vector2.one, Vector2.zero);
            }
#endif
        }
    }
}