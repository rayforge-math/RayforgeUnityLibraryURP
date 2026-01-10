using Rayforge.Core.Common;
using Rayforge.Core.Diagnostics;
using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Rayforge.URP.Utility.RendererFeatures.DepthPyramid
{
    /// <summary>
    /// ScriptableRendererFeature that generates a hierarchical depth pyramid for use in effects like SSAO or depth-based post-processing.
    /// </summary>
    public class DepthPyramidFeature : ScriptableRendererFeature
    {
        /// <summary>
        /// Maximum number of mip levels supported by the depth pyramid.
        /// </summary>
        public const int MipCountMax = DepthPyramidPass.MipCountMax;

        /// <summary>
        /// Name of the shader used to generate the depth pyramid.
        /// </summary>
        private const string k_ShaderName = "DepthPyramid";

        /// <summary>
        /// Full path to the depth pyramid shader within the project's resources.
        /// </summary>
        private static readonly string k_FullShaderName = ResourcePaths.ShaderResourceFolder + k_ShaderName;

        /// <summary>
        /// The type of input required from the camera for this render pass.
        /// Depth pyramid generation requires the camera's depth texture.
        /// </summary>
        private const ScriptableRenderPassInput k_PassInput = ScriptableRenderPassInput.Depth;

        /// <summary>
        /// Determines at which point in the camera's render pipeline the pass is injected.
        /// For the depth pyramid, it should be AfterRenderingPrePasses to ensure
        /// the camera depth texture is available but before lighting passes that may consume it.
        /// </summary>
        [SerializeField, InspectorName("Injection Point")]
        [Tooltip(
            "When the depth pyramid pass should be executed in the camera render pipeline.\n" +
            "- Recommended: AfterRenderingPrePasses, so the camera's depth texture is available.\n" +
            "- Not later (e.g., AfterRenderingOpaques), because other passes like SSAO, shadows, or post-processing " +
            "may need the depth pyramid earlier in the frame.\n" +
            "- Injecting too early may fail if the depth texture is not yet created."
        )]
        private RenderPassEvent m_InjectionPoint = RenderPassEvent.AfterRenderingPrePasses;

        /// <summary>
        /// Number of mip levels to generate for the depth pyramid.
        /// Must be between 1 and <see cref="MipCountMax"/>.
        /// </summary>
        [Range(1, MipCountMax), SerializeField, InspectorName("Mip Count")]
        [Tooltip("Number of mip levels to generate for the depth pyramid (1 = full resolution only).")]
        private int m_MipCount = 8;

        /// <summary>
        /// Number of mip levels to generate for the depth pyramid.
        /// Must be between 1 and <see cref="MipCountMax"/>.
        /// </summary>
        public int MipCount
        {
            get => m_MipCount;
            private set => m_MipCount = Math.Clamp(mipLevel, 1, MipCountMax);
        }

#if UNITY_EDITOR
        [Header("Debug")]

        /// <summary>
        /// Toggle to visualize the depth pyramid in the editor for debugging purposes.
        /// </summary>
        [Tooltip("Toggle to visualize the generated depth pyramid in the editor.")]
        public bool showDepthPyramid = false;

        /// <summary>
        /// The mip level to visualize when debugging.
        /// Only used if <see cref="showDepthPyramid"/> is enabled.
        /// </summary>
        [Range(0, MipCountMax - 1)]
        [Tooltip("Which mip level of the depth pyramid to display for debugging.")]
        public int mipLevel = 0;
#endif

#if UNITY_EDITOR
        public void OnValidate()
        {
            mipLevel = Math.Clamp(mipLevel, 0, MipCount - 1);
        }
#endif

        /// <summary>
        /// The render pass injection point in the pipeline.  
        /// Setting this property updates the internal render pass event immediately.
        ///
        /// <para>Default is <see cref="RenderPassEvent.AfterRenderingPrePasses"/>.  
        /// This is chosen because it ensures that the depth pyramid is generated **after the camera's pre-passes**, 
        /// but **before main opaque rendering**, making it available for any subsequent effects that rely on depth, 
        /// such as SSAO, depth-based post-processing, or motion vectors.
        /// Otherwise, depth values might be outdated or in an invalid state.
        /// </para>
        /// </summary>
        public RenderPassEvent InjectionPoint
        {
            get => m_InjectionPoint;
            set
            {
                m_InjectionPoint = value;
                if (m_RenderPass != null)
                    m_RenderPass.renderPassEvent = m_InjectionPoint;
            }
        }

        /// <summary>
        /// Ensures that the depth pyramid feature will generate at least the requested number of mip levels.
        /// Updates the <see cref="MipCount"/> slider.  
        /// By default, it will only increase the mip count; pass <paramref name="force"/> = true to also allow decreasing it.
        /// </summary>
        /// <param name="requestedMipCount">Number of mip levels including Mip0.</param>
        /// <param name="force">
        /// If true, the <see cref="MipCount"/> will be set exactly to <paramref name="requestedMipCount"/>,
        /// otherwise it will only increase if the current value is smaller.
        /// </param>
        public void EnsureMipCount(int requestedMipCount, bool force = false)
        {
            if (force || requestedMipCount > MipCount)
            {
                MipCount = requestedMipCount;
            }
        }

        [SerializeField, HideInInspector]
        private ComputeShader m_Shader;

        /// <summary>
        /// Internal instance of the DepthPyramidPass.
        /// </summary>
        private DepthPyramidPass m_RenderPass;

        /// <summary>
        /// Called when the renderer feature is created. Loads the compute shader and initializes the render pass.
        /// </summary>
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

        /// <summary>
        /// Adds the DepthPyramidPass to the renderer if the camera type is <see cref="CameraType.Game"/>.
        /// Configures the required inputs before enqueueing.
        /// </summary>
        /// <param name="renderer">The ScriptableRenderer instance.</param>
        /// <param name="renderingData">Current rendering data.</param>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_RenderPass == null) return;

            if (renderingData.cameraData.cameraType == CameraType.Game)
            {
                m_RenderPass.renderPassEvent = m_InjectionPoint;
                m_RenderPass.UpdateMipCount(m_MipCount);

                var passInput = k_PassInput;
#if UNITY_EDITOR
                m_RenderPass.UpdateDebugSettings(showDepthPyramid, mipLevel);
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

        /// <summary>
        /// Disposes the internal render pass and any unmanaged resources.
        /// </summary>
        /// <param name="disposing">Indicates whether the method is called from Dispose (true) or from a finalizer (false).</param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            m_RenderPass?.Dispose();
        }
    }
}
