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
    public static class DepthPyramidGlobals
    {
        private const string k_BaseName = "_" + Globals.CompanyName + "_DepthPyramidMip";

        /// <summary>
        /// Internal array storing all mip data together.
        /// </summary>
        private static DepthPyramidMip[] s_Mips = Array.Empty<DepthPyramidMip>();

        /// <summary>
        /// Generates or updates shader IDs, names, and texel sizes for the depth pyramid mips.
        /// Should be called once per resolution change.
        /// </summary>
        /// <param name="mipCount">Number of mip levels including mip 0.</param>
        /// <param name="baseRes">Base resolution (camera or input texture).</param>
        internal static void Generate(int mipCount, Vector2Int baseRes)
        {
            mipCount = Mathf.Clamp(mipCount, 1, DepthPyramidPass.MipCountMax);

            if (s_Mips.Length != mipCount)
            {
                var newMips = new DepthPyramidMip[mipCount];
                int copyCount = Math.Min(s_Mips.Length, mipCount);
                if (copyCount > 0)
                {
                    Array.Copy(s_Mips, newMips, copyCount);
                }
                s_Mips = newMips;
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
        }

        /// <summary>
        /// Returns the DepthPyramidMip struct for a given mip index.
        /// </summary>
        /// <param name="index">Mip index (0-based).</param>
        /// <returns>Immutable DepthPyramidMip struct containing IDs, name, and texel size.</returns>
        public static DepthPyramidMip GetMip(int index)
        {
            if (index < 0 || index >= s_Mips.Length)
                throw new ArgumentOutOfRangeException(nameof(index), $"Mip index {index} is out of range (0..{s_Mips.Length - 1})");

            return s_Mips[index];
        }

        /// <summary>
        /// Returns the number of mip levels currently generated.
        /// </summary>
        public static int Count => s_Mips.Length;

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
