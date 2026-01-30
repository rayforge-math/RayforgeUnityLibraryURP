using Rayforge.Core.Rendering.Passes;
using System;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace Rayforge.URP.Utility.RendererFeatures.DepthPyramid
{
    /// <summary>
    /// Frame-specific data for the Depth Pyramid, used within the URP RenderGraph context.
    /// This class acts as a bridge to transport <see cref="TextureHandleMeta{TextureHandle}"/>s between different 
    /// render passes while ensuring correct GPU resource synchronization (barriers).
    /// </summary>
    public class DepthPyramidFrameData : ContextItem
    {
        /// <summary>
        /// Metadata for the Near depth chain (closest points).
        /// Used for effects like SSAO, DoF, and Bilateral Upsampling.
        /// </summary>
        public TextureHandleMeta<TextureHandle>[] nearMips = new TextureHandleMeta<TextureHandle>[DepthPyramidProvider.MipCountMax];

        /// <summary>
        /// Metadata for the Far depth chain (furthest points).
        /// Used for performance optimizations like Empty Space Skipping in Raymarching.
        /// </summary>
        public TextureHandleMeta<TextureHandle>[] farMips = new TextureHandleMeta<TextureHandle>[DepthPyramidProvider.MipCountMax];

        /// <summary>
        /// The depth history from the PREVIOUS frame.
        /// Used for temporal effects, reprojection, and temporal stability.
        /// </summary>
        public TextureHandleMeta<TextureHandle> historyDepth;

        /// <summary>
        /// Resets the metadata for both chains at the beginning of each frame.
        /// Called automatically by the <see cref="ContextContainer"/>.
        /// </summary>
        public override void Reset()
        {
            // Clear both arrays to avoid accidental reuse of handles from the previous frame
            Array.Clear(nearMips, 0, nearMips.Length);
            Array.Clear(farMips, 0, farMips.Length);
        }
    }
}
