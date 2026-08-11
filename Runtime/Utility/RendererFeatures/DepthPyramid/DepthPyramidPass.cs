using Rayforge.Core.Execution.Abstractions;
using Rayforge.Core.Execution.Handler;
using Rayforge.Core.Rendering.Collections;
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
using static Rayforge.Core.Utility.RenderGraphs.Collections.HistoryRTHandles;

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

        /// <summary>
        /// Zero-allocation struct handler for creating and reallocating depth mip handles.
        /// </summary>
        private readonly struct DepthMipCreateHandler : IFunctionHandler<MipCreateContext<RTHandle>, bool>
        {
            public bool Execute(MipCreateContext<RTHandle> context)
            {
                return RenderingUtils.ReAllocateHandleIfNeeded(ref context.Handle, context.Descriptor, FilterMode.Point, TextureWrapMode.Clamp);
            }
        }

        private static readonly int kSourceId = Shader.PropertyToID("_Source");
        private static readonly int kDestId = Shader.PropertyToID("_Dest");
        private static readonly int kSourceResId = Shader.PropertyToID("_SourceRes");
        private static readonly int kDestResId = Shader.PropertyToID("_DestRes");

        private Vector2Int m_LastResolution = new Vector2Int(-1, -1);

        private bool m_RenderFarMips = false;
        private bool m_RenderNearMips = false;
        private bool m_RenderJitteredMips = false;
        private bool m_RenderHistory = false;

        private readonly UnsafeRTHandleMipChain m_FarHandles;
        private readonly UnsafeRTHandleMipChain m_NearHandles;
        private readonly UnsafeRTHandleMipChain m_JitteredHandles;
        private HistoryRTHandles m_HistoryHandles;

        private RenderTextureDescriptor m_Descriptor;
        private DepthPyramidPassData m_DownsamplePassData = new DepthPyramidPassData();
        private CopyPassData m_CopyPassData = new CopyPassData();

        private ComputeShader k_Shader;

        private PassMeta m_KernelMin;
        private PassMeta m_KernelMax;
        private PassMeta m_KernelJittered;
        private int k_CopyKernelId;

        private const string k_CopyKernel = "Copy";
        private const string k_DownsampleMinKernel = "DownsampleMin";
        private const string k_DownsampleMaxKernel = "DownsampleMax";
        private const string k_DownsampleJitteredKernel = "DownsampleJittered";

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
            m_KernelJittered = new PassMeta
            {
                meta = new ComputePassMeta(shader, k_DownsampleJitteredKernel),
                name = k_DownsampleJitteredKernel
            };
            m_Descriptor = DefaultDescriptors.DepthBufferFullScreen();

            // Initialize all chains using the parameterless constructor
            m_FarHandles = new UnsafeRTHandleMipChain();
            m_NearHandles = new UnsafeRTHandleMipChain();
            m_JitteredHandles = new UnsafeRTHandleMipChain();
            m_HistoryHandles = new HistoryRTHandles(null, null);
        }

        public void Dispose()
        {
            m_FarHandles.Dispose();
            m_NearHandles.Dispose();
            m_JitteredHandles.Dispose();
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
                    var handler = new DepthMipCreateHandler();
                    chain.CreateUnsafe(m_Descriptor, 1, requestedCount - 1, true, ref handler);
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

            var handler = new FuncHandler<RTAllocData, bool>(
                static (allocData) =>
                {
                    return RenderingUtils.ReAllocateHandleIfNeeded(ref allocData.Handle, allocData.descriptor, FilterMode.Point, TextureWrapMode.Clamp);
                });

            if (!isRequested && !anyChainActive)
            {
                m_HistoryHandles.Dispose();
                return false;
            }

            if (!isRequested)
            {
                m_HistoryHandles.ReAllocateTargetIfNeeded(m_Descriptor, ref handler);
                m_HistoryHandles.DisposeHistory();
                return false;
            }

            if (isRequested)
            {
                m_HistoryHandles.ReAllocateHandlesIfNeeded(m_Descriptor, ref handler);
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
            if (DepthPyramidProvider.IsDirty(DepthChainType.Jittered) || descChanged)
                m_RenderJitteredMips = UpdateDepthChain(m_JitteredHandles, DepthChainType.Jittered, baseRes, descChanged);

            if (DepthPyramidProvider.IsAnyDirty || descChanged)
                m_RenderHistory = UpdateHistory(baseRes, descChanged);

            DepthPyramidProvider.ResetDirty();

            if (!(m_RenderFarMips || m_RenderNearMips || m_RenderJitteredMips || m_RenderHistory))
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
            if (m_RenderJitteredMips)
            {
                m_JitteredHandles.SetHandleUnsafe(0, RTmip0);
                RecordChain(renderGraph, m_JitteredHandles, DepthChainType.Jittered, m_KernelJittered, baseRes, depthData.jitteredMips);
            }

#if UNITY_EDITOR
            if (m_DebugChainType != DepthChainType.None)
            {
                var debugChain = m_DebugChainType switch
                {
                    DepthChainType.Far => depthData.farMips,
                    DepthChainType.Jittered => depthData.jitteredMips,
                    _ => depthData.nearMips
                };

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