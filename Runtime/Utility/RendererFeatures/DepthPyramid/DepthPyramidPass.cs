using Rayforge.Core.Diagnostics;
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
    public class DepthPyramidPass : ScriptableRenderPass, IDisposable
    {
        private class DepthPyramidPassData : ComputePassData<DepthPyramidPassData>
        {
            public Vector2 sourceRes;
            public Vector2 destRes;
            public int chainIndex;

            public override void CopyUserData(DepthPyramidPassData other)
            {
                sourceRes = other.sourceRes;
                destRes = other.destRes;
                chainIndex = other.chainIndex;
            }
        }

        private static readonly int kSourceId = Shader.PropertyToID("_Source");
        private static readonly int kDestId = Shader.PropertyToID("_Dest");
        private static readonly int kSourceResId = Shader.PropertyToID("_SourceRes");
        private static readonly int kDestResId = Shader.PropertyToID("_DestRes");

        private Vector2Int m_LastResolution = new Vector2Int(-1, -1);

        private readonly RTHandleMipChain m_FarHandles;
        private readonly RTHandleMipChain m_NearHandles;

        private RenderTextureDescriptor m_Descriptor;
        private DepthPyramidPassData m_PassData = new DepthPyramidPassData();

        private PassMeta m_KernelMin;
        private PassMeta m_KernelMax;

        private const string k_DownsampleMinKernel = "DownsampleMin";
        private const string k_DownsampleMaxKernel = "DownsampleMax";

        private struct PassMeta
        {
            public ComputePassMeta meta;
            public string name;
        }

#if UNITY_EDITOR
        private bool m_Debug = false;
        private DepthChainType m_DebugChainType = DepthChainType.Far;
        private int m_DebugMipLevel = 0;
#endif

        public DepthPyramidPass(ComputeShader shader)
        {
            m_KernelMin = new PassMeta
            {
                meta = new ComputePassMeta(shader, k_DownsampleMinKernel),
                name = k_DownsampleMinKernel
            };
            m_KernelMax = new PassMeta
            {
                meta = new ComputePassMeta(shader, k_DownsampleMaxKernel),
                name = k_DownsampleMaxKernel
            };
            m_Descriptor = DefaultDescriptors.DepthBufferFullScreen();

            // Initialize all chains
            m_FarHandles = CreateChain();
            m_NearHandles = CreateChain();
        }

        private RTHandleMipChain CreateChain() => new RTHandleMipChain((ref RTHandle handle, RenderTextureDescriptor desc, int mip) =>
            RenderingUtils.ReAllocateHandleIfNeeded(ref handle, desc, FilterMode.Point, TextureWrapMode.Clamp)
        );

        public void Dispose()
        {
            m_FarHandles.Dispose();
            m_NearHandles.Dispose();
        }

#if UNITY_EDITOR
        internal void UpdateDebugSettings(bool show, DepthChainType chainType, int mipLevel)
        {
            m_Debug = show;
            m_DebugChainType = chainType;
            m_DebugMipLevel = mipLevel;
        }
#endif

        /// <summary>
        /// Checks if the RTHandles for a specific chain need to be resized or recreated based on camera resolution or provider settings.
        /// </summary>
        /// <param name="chain">The RTHandle mip chain to update.</param>
        /// <param name="type">The depth chain type (Min/Max).</param>
        /// <param name="baseRes">Base resolution of the camera.</param>
        /// <returns>True if the resolution or mip count has changed and the RTHandles were updated, false otherwise.</returns>
        private bool CheckAndUpdateChain(RTHandleMipChain chain, DepthChainType type, Vector2Int baseRes)
        {
            bool changed = false;

            if (m_LastResolution != baseRes)
            {
                m_Descriptor.width = baseRes.x;
                m_Descriptor.height = baseRes.y;
                changed = true;
            }

            var requestedCount = DepthPyramidProvider.GetRequestedCount(type);

            if (chain.MipCount != requestedCount)
            {
                if (requestedCount > 0)
                    chain.Create(m_Descriptor, requestedCount);
                else
                    chain.Resize(0);

                changed = true;
            }

            if (changed)
                DepthPyramidProvider.Generate(type, baseRes);

            DepthPyramidProvider.ResetDirty(type);
            return changed;
        }

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

            if (CheckAndUpdateChain(m_FarHandles, DepthChainType.Far, baseRes))
                DepthPyramidProvider.SetGlobalDepthPyramid(DepthChainType.Far, m_FarHandles, false);

            if (CheckAndUpdateChain(m_NearHandles, DepthChainType.Near, baseRes))
                DepthPyramidProvider.SetGlobalDepthPyramid(DepthChainType.Near, m_NearHandles, true);

            m_LastResolution = baseRes;

            PassMeta farKernel = DepthPyramidProvider.IsReversedZ ? m_KernelMin : m_KernelMax;
            PassMeta nearKernel = DepthPyramidProvider.IsReversedZ ? m_KernelMax : m_KernelMin;

            RecordChain(renderGraph, m_FarHandles, DepthChainType.Far, farKernel, srcDepthBuffer, baseRes, depthData.farMips);
            RecordChain(renderGraph, m_NearHandles, DepthChainType.Near, nearKernel, srcDepthBuffer, baseRes, depthData.nearMips);

#if UNITY_EDITOR
            if (m_Debug)
            {
                var debugChain = m_DebugChainType == DepthChainType.Far ? m_FarHandles : m_NearHandles;
                if (m_DebugMipLevel < debugChain.MipCount)
                {
                    TextureHandle debugHandle = debugChain[m_DebugMipLevel].ToRenderGraphHandle(renderGraph);
                    renderGraph.AddBlitPass(debugHandle, resourceData.activeColorTexture, Vector2.one, Vector2.zero);
                }
            }
#endif
        }

        /// <summary>
        /// Internal helper to record the blit and compute passes for a specific depth chain.
        /// </summary>
        private void RecordChain(RenderGraph renderGraph, RTHandleMipChain handles, DepthChainType type, PassMeta kernel, TextureHandle srcDepth, Vector2Int baseRes, TextureHandleMeta<TextureHandle>[] contextMips)
        {
            if (handles.MipCount == 0) return;

            var firstMip = handles[0].ToRenderGraphHandle(renderGraph);
            renderGraph.AddBlitPass(srcDepth, firstMip, Vector2.one, Vector2.zero);

            if (contextMips != null)
            {
                contextMips[0] = new TextureHandleMeta<TextureHandle>
                {
                    Handle = firstMip,
                    Meta = DepthPyramidProvider.GetMip(type, 0).Meta
                };
            }

            TextureHandle prevMip = firstMip;
            Vector2 prevRes = baseRes;

            // Generate subsequent mips using the specialized compute kernel
            for (int i = 1; i < handles.MipCount; ++i)
            {
                var curMip = handles[i].ToRenderGraphHandle(renderGraph);
                if (!curMip.IsValid()) break;

                var mipData = DepthPyramidProvider.GetMip(type, i);
                Vector4 texelSize = mipData.Meta.TexelSize;
                Vector2 curRes = new Vector2(texelSize.z, texelSize.w);

                var passMeta = kernel.meta;
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

                RenderPassRecorder.AddComputePass(renderGraph, kernel.name, m_PassData);

                if (contextMips != null)
                {
                    contextMips[i] = new TextureHandleMeta<TextureHandle>
                    {
                        Handle = curMip,
                        Meta = mipData.Meta
                    };
                }

                prevMip = curMip;
                prevRes = curRes;
            }
        }
    }
}