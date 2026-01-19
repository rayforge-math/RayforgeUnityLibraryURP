using Rayforge.Core.Common;
using Rayforge.Core.Rendering.Collections.Helpers;
using Rayforge.Core.Rendering.Passes;
using System;
using UnityEngine;

namespace Rayforge.URP.Utility.RendererFeatures.DepthPyramid
{
    /// <summary>
    /// Represents all relevant data for a single mip level of the depth pyramid.
    /// This struct is immutable from outside to prevent accidental modification.
    /// </summary>
    public readonly struct DepthPyramidMip
    {
        /// <summary>
        /// Shader property IDs for this mip level (texture + texelSize).
        /// </summary>
        public readonly TextureIds Ids;

        /// <summary>
        /// Human-readable name for this mip level (for debug / MaterialPropertyBlock).
        /// </summary>
        public readonly string Name;

        /// <summary>
        /// Texel size vector for this mip level.
        /// x = 1/width, y = 1/height, z = width, w = height.
        /// </summary>
        public readonly Vector4 TexelSize;

        internal DepthPyramidMip(TextureIds ids, string name, Vector4 texelSize)
        {
            Ids = ids;
            Name = name;
            TexelSize = texelSize;
        }
    }

    /// <summary>
    /// Manages shader property IDs, names, and texel sizes for all mips of a depth pyramid.
    /// Safe: All data is private, arrays are only resized when needed, and values cannot be modified externally.
    /// </summary>
    public static class DepthPyramidProvider
    {
        /// <summary>
        /// Maximum number of mip levels supported.
        /// </summary>
        public const int MipCountMax = 16;

        private const string k_BaseName = "_" + Globals.CompanyName + "_DepthPyramidMip";

        private static int m_MipCount = 1;
        private static bool m_MipCountDirty = false;

        /// <summary>
        /// Indicates whether the mip count has changed since the last reset.
        /// Used by the rendering feature to trigger texture re-allocation.
        /// </summary>
        internal static bool MipCountDirty => m_MipCountDirty;

        /// <summary>
        /// Resets the dirty flag. Should be called by the render pass after handling re-allocation.
        /// </summary>
        internal static void ResetMipCountDirty() => m_MipCountDirty = false;

        /// <summary>
        /// Clamps the requested mip count to the valid range.
        /// </summary>
        /// <param name="requestedMipCount">The value to clamp.</param>
        /// <returns>A value between 1 and <see cref="MipCountMax"/>.</returns>
        private static int ClampToValidMipRange(int requestedMipCount)
            => Math.Clamp(requestedMipCount, 1, MipCountMax);

        /// <summary>
        /// The currently requested number of mip levels to be generated for the depth pyramid (including Mip 0).
        /// This represents the target state. Changing this value automatically sets <see cref="MipCountDirty"/> to true.
        /// </summary>
        /// <remarks>
        /// Use <see cref="Count"/> to get the number of mips that are actually allocated and ready for rendering.
        /// </remarks>
        /// <value>Integer between 1 and <see cref="MipCountMax"/>.</value>
        public static int MipCount
        {
            get => m_MipCount;
            set
            {
                value = ClampToValidMipRange(value);
                if (m_MipCount != value)
                {
                    m_MipCount = value;
                    m_MipCountDirty = true;
                }
            }
        }

        /// <summary>
        /// Ensures that the depth pyramid feature will generate at least the requested number of mip levels.
        /// Useful for calling from <c>AddRenderPasses</c> in other features.
        /// </summary>
        /// <param name="requestedMipCount">Desired number of mip levels (including full-res Mip 0).</param>
        /// <param name="force">
        /// If <c>true</c>, <see cref="MipCount"/> is set exactly to <paramref name="requestedMipCount"/>.
        /// If <c>false</c> (default), it only increases the count if the current value is smaller.
        /// </param>
        public static void EnsureMipCount(int requestedMipCount, bool force = false)
        {
            requestedMipCount = ClampToValidMipRange(requestedMipCount);

            if (force || requestedMipCount > m_MipCount)
            {
                MipCount = requestedMipCount;
            }
        }

        /// <summary>
        /// Internal storage for all generated mip data.
        /// </summary>
        private static DepthPyramidMip[] s_Mips = Array.Empty<DepthPyramidMip>();

        /// <summary>
        /// The current base resolution used for the generated mips. 
        /// Helps to avoid recalculating if the resolution hasn't changed.
        /// </summary>
        private static Vector2Int s_CurrentBaseRes;

        /// <summary>
        /// Generates or updates shader IDs, names, and texel sizes for the depth pyramid mips.
        /// Uses the current <see cref="MipCount"/>.
        /// </summary>
        /// <param name="baseRes">Base resolution (usually camera or screen resolution).</param>
        internal static void Generate(Vector2Int baseRes)
        {
            int mipCount = m_MipCount;

            if (s_Mips.Length == mipCount && s_CurrentBaseRes == baseRes)
                return;

            s_CurrentBaseRes = baseRes;

            if (s_Mips.Length != mipCount)
            {
                s_Mips = new DepthPyramidMip[mipCount];
            }

            for (int i = 0; i < mipCount; i++)
            {
                string name = $"{k_BaseName}{i}";
                string texelName = $"{name}_TexelSize";

                Vector2Int mipRes = GetMipResolution(i, baseRes);
                Vector4 texelSize = new Vector4(
                    1f / mipRes.x,
                    1f / mipRes.y,
                    mipRes.x,
                    mipRes.y
                );

                TextureIds ids = new TextureIds
                {
                    texture = Shader.PropertyToID(name),
                    texelSize = Shader.PropertyToID(texelName)
                };

                s_Mips[i] = new DepthPyramidMip(ids, name, texelSize);
            }

            // After successfully regenerating, we can consider the state clean
            ResetMipCountDirty();
        }

        /// <summary>
        /// Returns the <see cref="DepthPyramidMip"/> data for a specific mip index.
        /// </summary>
        /// <param name="index">Mip index (0-based).</param>
        /// <returns>Immutable struct containing shader IDs, names, and texel sizes.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if index is outside the current <see cref="Count"/>.</exception>
        public static DepthPyramidMip GetMip(int index)
        {
            if (index < 0 || index >= ActiveMipCount)
                throw new ArgumentOutOfRangeException(nameof(index), $"Mip index {index} is out of range (0..{ActiveMipCount - 1})");

            return s_Mips[index];
        }

        /// <summary>
        /// The number of mip levels currently allocated and ready for use.
        /// </summary>
        public static int ActiveMipCount => s_Mips.Length;

        /// <summary>
        /// Computes the resolution of a specific mip level for a given base resolution.
        /// </summary>
        /// <param name="mipIndex">Mip index (0 = full resolution).</param>
        /// <param name="baseRes">Base resolution (e.g., camera size).</param>
        /// <returns>Resolution of the mip as Vector2Int.</returns>
        public static Vector2Int GetMipResolution(int mipIndex, Vector2Int baseRes)
            => MipChainHelpers.DefaultMipResolution(mipIndex, baseRes);
    }
}
