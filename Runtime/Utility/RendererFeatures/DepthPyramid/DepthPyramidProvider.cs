using Rayforge.Core.Common;
using Rayforge.Core.Rendering.Collections.Helpers;
using Rayforge.Core.Rendering.Passes;
using Rayforge.Core.Utility.RenderGraphs.Collections;
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rayforge.URP.Utility.RendererFeatures.DepthPyramid
{
    /// <summary>
    /// Central data provider for the Depth Pyramid. 
    /// Manages metadata and texture references synchronously within a single structure.
    /// </summary>
    public static class DepthPyramidProvider
    {
        /// <summary> Maximum number of supported mip levels. </summary>
        public const int MipCountMax = 16;

        private const string k_BaseName = "_" + Globals.CompanyName + "_DepthPyramidMip";

        private static int s_MipCount = 1;
        private static bool s_MipCountDirty = false;
        private static Vector2Int s_CurrentBaseRes;
        private static TextureHandleMeta<RTHandle>[] s_Mips = Array.Empty<TextureHandleMeta<RTHandle>>();

        /// <summary> 
        /// Returns true if the mip count has changed since the last reset. 
        /// Used by RenderFeatures to trigger re-allocations.
        /// </summary>
        internal static bool MipCountDirty => s_MipCountDirty;

        /// <summary> 
        /// The currently requested number of mip levels (including Mip 0).
        /// Changing this value sets <see cref="MipCountDirty"/> to true.
        /// </summary>
        public static int MipCount
        {
            get => s_MipCount;
            set
            {
                value = Math.Clamp(value, 1, MipCountMax);
                if (s_MipCount != value)
                {
                    s_MipCount = value;
                    s_MipCountDirty = true;
                }
            }
        }

        /// <summary> The number of currently allocated and valid mip levels. </summary>
        public static int ActiveMipCount => s_Mips.Length;

        /// <summary> 
        /// Provides high-performance read-only access to all mip data via Span.
        /// </summary>
        public static ReadOnlySpan<TextureHandleMeta<RTHandle>> Mips => s_Mips;

        /// <summary> Resets the dirty flag for the mip count. </summary>
        internal static void ResetMipCountDirty() => s_MipCountDirty = false;

        /// <summary>
        /// Ensures the pyramid generates at least the requested number of mips.
        /// </summary>
        /// <param name="requestedMipCount">Desired count (1 to 16).</param>
        /// <param name="force">If true, sets the value exactly; otherwise, only increases it.</param>
        public static void EnsureMipCount(int requestedMipCount, bool force = false)
        {
            requestedMipCount = Math.Clamp(requestedMipCount, 1, MipCountMax);
            if (force || requestedMipCount > s_MipCount) MipCount = requestedMipCount;
        }

        /// <summary>
        /// Retrieves the data for a specific mip level.
        /// </summary>
        /// <param name="index">0 for full-res, 1+ for downsampled levels.</param>
        /// <returns>The <see cref="DepthPyramidMip"/> structure.</returns>
        public static TextureHandleMeta<RTHandle> GetMip(int index)
        {
            if (index < 0 || index >= s_Mips.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return s_Mips[index];
        }

        /// <summary>
        /// Calculates shader IDs and texel sizes based on resolution.
        /// Texture handles are initialized as null until <see cref="SetGlobalDepthPyramid"/> is called.
        /// </summary>
        /// <param name="baseRes">Base resolution (usually the camera pixel rect).</param>
        internal static void Generate(Vector2Int baseRes)
        {
            if (s_Mips.Length == s_MipCount && s_CurrentBaseRes == baseRes)
                return;

            s_CurrentBaseRes = baseRes;
            s_Mips = new TextureHandleMeta<RTHandle>[s_MipCount];

            for (int i = 0; i < s_MipCount; i++)
            {
                string name = $"{k_BaseName}{i}";
                Vector2Int mipRes = MipChainHelpers.DefaultMipResolution(i, baseRes);

                var ids = new TextureIds
                {
                    texture = Shader.PropertyToID(name),
                    texelSize = Shader.PropertyToID($"{name}_TexelSize")
                };

                var texelSize = new Vector4(1f / mipRes.x, 1f / mipRes.y, (float)mipRes.x, (float)mipRes.y);
                s_Mips[i] = new TextureHandleMeta<RTHandle>(ids, name, texelSize, null);
            }
            ResetMipCountDirty();
        }

        /// <summary>
        /// Links RTHandles to the mip structures and binds them as global shader variables.
        /// </summary>
        /// <param name="depthPyramidHandles">The chain of downsampled mips (for indices 1 to N).</param>
        /// <param name="sourceDepth">The original camera depth texture (for index 0).</param>
        /// <exception cref="ArgumentNullException">Thrown if depthPyramidHandles is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the chain length does not match expected mip count.</exception>
        internal static void SetGlobalDepthPyramid(RTHandleMipChain depthPyramidHandles)
        {
            if (depthPyramidHandles == null) throw new ArgumentNullException(nameof(depthPyramidHandles));

            int expected = s_Mips.Length;
            if (depthPyramidHandles.MipCount != expected)
                throw new InvalidOperationException($"Mip-Chain mismatch. Provider: {expected}, Chain: {depthPyramidHandles.MipCount}");

            for (int i = 0; i < expected; i++)
            {
                RTHandle currentHandle = depthPyramidHandles[i];

                // Re-create the struct with the handle (since it's a readonly struct)
                var m = s_Mips[i];
                s_Mips[i] = new TextureHandleMeta<RTHandle>(m.Meta, currentHandle);

                // Global GPU binding
                Shader.SetGlobalVector(s_Mips[i].Meta.Ids.texelSize, s_Mips[i].Meta.TexelSize);
                if (currentHandle != null)
                {
                    Shader.SetGlobalTexture(s_Mips[i].Meta.Ids.texture, currentHandle);
                }
            }
        }
    }
}
