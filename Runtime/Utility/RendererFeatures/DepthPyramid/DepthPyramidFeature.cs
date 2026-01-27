using Rayforge.Core.Common;
using Rayforge.Core.Diagnostics;
using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Rayforge.URP.Utility.RendererFeatures.DepthPyramid
{
    public class DepthPyramidFeature : ScriptableRendererFeature
    {
        public const int MipCountMax = DepthPyramidProvider.MipCountMax;
        private const string k_ShaderName = "DepthPyramid";
        private static readonly string k_FullShaderName = ResourcePaths.ShaderResourceFolder + k_ShaderName;
        private const ScriptableRenderPassInput k_PassInput = ScriptableRenderPassInput.Depth;

        [SerializeField, InspectorName("Injection Point")]
        [Tooltip("Recommended: AfterRenderingOpaques.")]
        private RenderPassEvent m_InjectionPoint = RenderPassEvent.AfterRenderingOpaques;

        [Header("Chain Settings")]
        [Range(1, MipCountMax), SerializeField, InspectorName("Near Mips")]
        private int m_NearMipCount = 1;

        [Range(1, MipCountMax), SerializeField, InspectorName("Far Mips")]
        private int m_FarMipCount = 1;

#if UNITY_EDITOR
        [Header("Debug")]
        [Tooltip("Toggle to visualize a specific depth chain in the editor.")]
        public bool showDepthPyramid = false;

        [Tooltip("Which chain type to visualize.")]
        public DepthChainType debugChainType = DepthChainType.Near;

        [Range(0, MipCountMax - 1)]
        [Tooltip("Which mip level of the selected chain to display.")]
        public int mipLevel = 0;
#endif

#if UNITY_EDITOR
        public void OnValidate()
        {
            UdpateMipCount();
            UdpateMipLevel();
        }

        private void UdpateMipCount()
        {
            m_NearMipCount = UpdateMipCount(DepthChainType.Near, m_NearMipCount);
            m_FarMipCount = UpdateMipCount(DepthChainType.Far, m_FarMipCount);
        }

        private int UpdateMipCount(DepthChainType type, int mipCount)
        {
            var dirty = DepthPyramidProvider.IsDirty(type);
            var current = DepthPyramidProvider.GetRequestedCount(type);

            if(mipCount != current)
            {
                if (dirty)
                {
                    mipCount = current;
                }
                else
                {
                    DepthPyramidProvider.EnsureMipCount(type, mipCount, true);
                }
            }
            return mipCount;
        }

        private void UdpateMipLevel()
        {
            int activeMax = debugChainType switch
            {
                DepthChainType.Near => m_NearMipCount,
                DepthChainType.Far => m_FarMipCount,
                _ => 1
            };
            mipLevel = Math.Clamp(mipLevel, 0, Math.Max(0, activeMax - 1));
        }
#endif

        [SerializeField, HideInInspector]
        private ComputeShader m_Shader;
        private DepthPyramidPass m_RenderPass;

        public override void Create()
        {
            if (m_Shader == null)
            {
                m_Shader = UnityEngine.Resources.Load<ComputeShader>(k_FullShaderName);
                Assertions.NotNull(m_Shader, "Shader " + k_FullShaderName + " is null");
            }

            if (m_Shader != null)
            {
                m_RenderPass?.Dispose();
                m_RenderPass = new DepthPyramidPass(m_Shader)
                {
                    renderPassEvent = m_InjectionPoint
                };
            }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_RenderPass == null) return;

            if (renderingData.cameraData.cameraType == CameraType.Game)
            {
                m_RenderPass.renderPassEvent = m_InjectionPoint;

                var passInput = k_PassInput;
#if UNITY_EDITOR

                UdpateMipCount();
                UdpateMipLevel();
                m_RenderPass.UpdateDebugSettings(showDepthPyramid, debugChainType, mipLevel);

                if (showDepthPyramid)
                {
                    m_RenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
                    passInput |= ScriptableRenderPassInput.Color;
                }
#endif

                m_RenderPass.ConfigureInput(passInput);
                renderer.EnqueuePass(m_RenderPass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            m_RenderPass?.Dispose();
        }
    }
}
