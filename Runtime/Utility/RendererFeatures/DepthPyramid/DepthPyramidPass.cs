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

        private class CopyPassData : ComputePassData<CopyPassData>
        {
            public Vector2 destRes;

            public override void CopyUserData(CopyPassData other)
            {
                destRes = other.destRes;
            }
        }

        private static readonly int kSourceId = Shader.PropertyToID("_Source");
        private static readonly int kDestId = Shader.PropertyToID("_Dest");
        private static readonly int kSourceResId = Shader.PropertyToID("_SourceRes");
        private static readonly int kDestResId = Shader.PropertyToID("_DestRes");

        private Vector2Int m_LastResolution = new Vector2Int(-1, -1);

        private bool m_RenderFarMips = false;
        private bool m_RenderNearMips = false;
        private bool m_RenderHistory = false;

        private readonly UnsafeRTHandleMipChain m_FarHandles;
        private readonly UnsafeRTHandleMipChain m_NearHandles;
        private HistoryRTHandles m_HistoryHandles;

        private RenderTextureDescriptor m_Descriptor;
        private DepthPyramidPassData m_DownsamplePassData = new DepthPyramidPassData();
        private CopyPassData m_CopyPassData = new CopyPassData();

        private ComputeShader k_Shader;

        private PassMeta m_KernelMin;
        private PassMeta m_KernelMax;
        private int k_CopyKernelId;

        private const string k_CopyKernel = "Copy";
        private const string k_DownsampleMinKernel = "DownsampleMin";
        private const string k_DownsampleMaxKernel = "DownsampleMax";

        private struct PassMeta
        {
            public ComputePassMeta meta;
            public string name;
        }

#if UNITY_EDITOR
        private DepthChainType m_DebugChainType = DepthChainType.None;
        private int m_DebugMipLevel = 0;
#endif

        public DepthPyramidPass(ComputeShader shader)
        {
            k_Shader = shader;

            k_CopyKernelId = shader.FindKernel(k_CopyKernel);
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
            m_HistoryHandles = new HistoryRTHandles(
                (ref RTHandle handle, RenderTextureDescriptor desc, string name) => RenderingUtils.ReAllocateHandleIfNeeded(ref handle, desc, FilterMode.Point, TextureWrapMode.Clamp),
                null, null);
        }

        private UnsafeRTHandleMipChain CreateChain() => new UnsafeRTHandleMipChain(
            (ref RTHandle handle, RenderTextureDescriptor desc, int mip) => RenderingUtils.ReAllocateHandleIfNeeded(ref handle, desc, FilterMode.Point, TextureWrapMode.Clamp),
            (ref RTHandle handle) => { RTHandles.Release(handle); }
        );

        public void Dispose()
        {
            m_FarHandles.Dispose();
            m_NearHandles.Dispose();
            m_HistoryHandles.Dispose();
        }

#if UNITY_EDITOR
        internal void UpdateDebugSettings(DepthChainType chainType, int mipLevel)
        {
            m_DebugChainType = chainType;
            m_DebugMipLevel = mipLevel;
        }
#endif

        private bool UpdateDescriptor(Vector2Int baseRes)
        {
            if (m_LastResolution != baseRes)
            {
                m_Descriptor.width = baseRes.x;
                m_Descriptor.height = baseRes.y;

                m_LastResolution = baseRes;
                return true;
            }

            return false;
        }

        private bool UpdateDepthChain(UnsafeRTHandleMipChain chain, DepthChainType type, Vector2Int baseRes, bool descChanged)
        {
            var requestedCount = DepthPyramidProvider.GetRequestedCount(type);
            bool needsRecreation = descChanged || (chain.MipCount != requestedCount);

            if (needsRecreation)
            {
                if (requestedCount > 0)
                {
                    chain.CreateUnsafe(m_Descriptor, 1, requestedCount - 1, true);
                }
                else
                {
                    chain.Resize(0);
                }

                DepthPyramidProvider.GenerateChainMeta(type, baseRes);
            }

            return chain.MipCount > 0;
        }

        private bool UpdateHistory(Vector2Int baseRes, bool descChanged)
        {
            bool isRequested = DepthPyramidProvider.IsHistoryRequested;
            bool anyChainActive = m_RenderFarMips || m_RenderNearMips;

            if (!isRequested && !anyChainActive)
            {
                m_HistoryHandles.Dispose();
                return false;
            }

            if (!isRequested)
            {
                m_HistoryHandles.ReAllocateTargetIfNeeded(m_Descriptor);
                m_HistoryHandles.DisposeHistory();
                return false;
            }

            if (isRequested)
            {
                m_HistoryHandles.ReAllocateHandlesIfNeeded(m_Descriptor);
                DepthPyramidProvider.GenerateHistoryMeta(baseRes);
                return true;
            }

            return false;
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

            bool descChanged = UpdateDescriptor(baseRes);

            if (DepthPyramidProvider.IsDirty(DepthChainType.Far) || descChanged)
                m_RenderFarMips = UpdateDepthChain(m_FarHandles, DepthChainType.Far, baseRes, descChanged);
            if (DepthPyramidProvider.IsDirty(DepthChainType.Near) || descChanged)
                m_RenderNearMips = UpdateDepthChain(m_NearHandles, DepthChainType.Near, baseRes, descChanged);

            if (DepthPyramidProvider.IsAnyDirty || descChanged)
                m_RenderHistory = UpdateHistory(baseRes, descChanged);

            DepthPyramidProvider.ResetDirty();

            if (!(m_RenderFarMips || m_RenderNearMips || m_RenderHistory))
                return;

            if (m_RenderHistory)
            {
                m_HistoryHandles.Swap();
                DepthPyramidProvider.SetHistoryDepth(m_HistoryHandles.History);
                var meta = DepthPyramidProvider.GetHistoryDepth();
                depthData.historyDepth = new TextureHandleMeta<TextureHandle>
                {
                    Handle = m_HistoryHandles.History.ToRenderGraphHandle(renderGraph),
                    Meta = meta.Meta
                };
            }

            var RTmip0 = m_HistoryHandles.Target;
            var mip0 = RTmip0.ToRenderGraphHandle(renderGraph);

            RecordCopyPass(renderGraph, srcDepthBuffer, mip0, baseRes);

            PassMeta farKernel = DepthPyramidProvider.IsReversedZ ? m_KernelMin : m_KernelMax;
            PassMeta nearKernel = DepthPyramidProvider.IsReversedZ ? m_KernelMax : m_KernelMin;

            if (m_RenderFarMips)
            {
                m_FarHandles.SetHandleUnsafe(0, RTmip0);
                RecordChain(renderGraph, m_FarHandles, DepthChainType.Far, farKernel, baseRes, depthData.farMips);
            }
            if (m_RenderNearMips)
            {
                m_NearHandles.SetHandleUnsafe(0, RTmip0);
                RecordChain(renderGraph, m_NearHandles, DepthChainType.Near, nearKernel, baseRes, depthData.nearMips);
            }

#if UNITY_EDITOR
            if (m_DebugChainType != DepthChainType.None)
            {
                var debugChain = m_DebugChainType == DepthChainType.Far ? depthData.farMips : depthData.nearMips;
                if (m_DebugMipLevel < debugChain.Length)
                {
                    TextureHandle debugHandle = debugChain[m_DebugMipLevel].Handle;
                    if (debugHandle.IsValid())
                        renderGraph.AddBlitPass(debugHandle, resourceData.activeColorTexture, Vector2.one, Vector2.zero);
                }
            }
#endif
        }

        private void RecordCopyPass(RenderGraph renderGraph, TextureHandle source, TextureHandle dest, Vector2Int resolution)
        {
            var passMeta = new ComputePassMeta(k_Shader, k_CopyKernelId);
            passMeta.ThreadGroupsX = Mathf.CeilToInt(resolution.x / 8.0f);
            passMeta.ThreadGroupsY = Mathf.CeilToInt(resolution.y / 8.0f);
            m_CopyPassData.PassMeta = passMeta;
            m_CopyPassData.PushInput(source, kSourceId);
            m_CopyPassData.PushDestination(dest, kDestId);
            m_CopyPassData.destRes = resolution;
            m_CopyPassData.RenderFuncUpdate = static (cmd, data) =>
            {
                cmd.SetComputeVectorParam(data.PassMeta.Shader, kDestResId, data.destRes);
            };
            RenderPassRecorder.AddComputePass(renderGraph, k_CopyKernel, m_CopyPassData);
        }

        private void RecordChain(RenderGraph renderGraph, UnsafeRTHandleMipChain handles, DepthChainType type, PassMeta kernel, Vector2Int baseRes, TextureHandleMeta<TextureHandle>[] contextMips)
        {
            if (handles.MipCount == 0) return;

            var firstMip = handles[0].ToRenderGraphHandle(renderGraph);

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
                m_DownsamplePassData.PassMeta = passMeta;
                m_DownsamplePassData.PushInput(prevMip, kSourceId);
                m_DownsamplePassData.PushDestination(curMip, kDestId);
                m_DownsamplePassData.sourceRes = prevRes;
                m_DownsamplePassData.destRes = curRes;
                m_DownsamplePassData.RenderFuncUpdate = static (cmd, data) =>
                {
                    cmd.SetComputeVectorParam(data.PassMeta.Shader, kSourceResId, data.sourceRes);
                    cmd.SetComputeVectorParam(data.PassMeta.Shader, kDestResId, data.destRes);
                };
                RenderPassRecorder.AddComputePass(renderGraph, kernel.name, m_DownsamplePassData);

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

            DepthPyramidProvider.SetGlobalDepthPyramid(type, handles, type == DepthChainType.Near);
        }
    }
}