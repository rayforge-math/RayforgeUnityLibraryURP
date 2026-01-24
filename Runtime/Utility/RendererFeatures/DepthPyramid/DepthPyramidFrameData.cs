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
        /// Array of render graph texture metadata for each mip level.
        /// These handles are crucial for the RenderGraph to track read/write dependencies and 
        /// to insert necessary GPU memory barriers between the generator and consumer passes.
        /// </summary>
        public TextureHandleMeta<TextureHandle>[] mips = new TextureHandleMeta<TextureHandle>[DepthPyramidProvider.MipCountMax];

        /// <summary>
        /// Resets the metadata at the beginning of each frame.
        /// Called automatically by the <see cref="ContextContainer"/>.
        /// </summary>
        public override void Reset()
        {
            Array.Clear(mips, 0, mips.Length);
        }
    }
}
