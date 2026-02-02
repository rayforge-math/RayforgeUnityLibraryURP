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
        private const ScriptableRenderPassInput k_PassInput = ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color;

        [SerializeField, InspectorName("Injection Point")]
        [Tooltip("Recommended: AfterRenderingOpaques.")]
        private RenderPassEvent m_InjectionPoint = RenderPassEvent.AfterRenderingOpaques;

        [Header("Chain Settings")]
        [Range(0, MipCountMax), SerializeField, InspectorName("Near Mips")]
        private int m_NearMipCount = -1;

        [Range(0, MipCountMax), SerializeField, InspectorName("Far Mips")]
        private int m_FarMipCount = -1;

#if UNITY_EDITOR
        [Header("Debug")]

        [Tooltip("Which chain type to visualize.")]
        public DepthChainType debugChainType = DepthChainType.Near;

        [Range(0, MipCountMax - 1)]
        [Tooltip("Which mip level of the selected chain to display.")]
        public int mipLevel = 0;
#endif

#if UNITY_EDITOR
        private void OnValidate()
        {
            UpdateUI();
        }

        private void UpdateUI()
        {
            UdpateMipCount();
            UdpateMipLevel();
        }

        private void UdpateMipCount()
        {
            m_NearMipCount = DepthPyramidProvider.GetRequestedCount(DepthChainType.Near);
            m_FarMipCount = DepthPyramidProvider.GetRequestedCount(DepthChainType.Far);
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

#if UNITY_EDITOR
                UpdateUI();
                m_RenderPass.UpdateDebugSettings(debugChainType, mipLevel);

                if (debugChainType != DepthChainType.None)
                {
                    m_RenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
                }
#endif

                m_RenderPass.ConfigureInput(k_PassInput);
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
