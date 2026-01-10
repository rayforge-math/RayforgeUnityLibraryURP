using Rayforge.Core.Common;
using Rayforge.Core.Rendering.Passes;
using System;
using UnityEngine;

namespace Rayforge.URP.Utility.RendererFeatures.DepthPyramid
{
    /// <summary>
    /// Generates and stores shader property IDs for each mip level of the depth pyramid.
    /// Provides convenience accessors for individual mips.
    /// </summary>
    public static class DepthPyramidGlobals
    {
        private const string k_BaseName = "_" + Globals.CompanyName + "_DepthPyramidMip";

        /// <summary>
        /// Shader property IDs for each mip level texture.
        /// </summary>
        public static TextureIds[] Ids { get; private set; } = Array.Empty<TextureIds>();

        /// <summary>
        /// Raw names for each mip level (e.g., for debug or MaterialPropertyBlock).
        /// </summary>
        public static string[] Names { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Generates IDs and names for the specified number of mips.
        /// Only callable internally by the framework.
        /// </summary>
        /// <param name="mipCount">Number of mips, including mip 0.</param>
        internal static void Generate(int mipCount)
        {
            mipCount = Mathf.Clamp(mipCount, 1, DepthPyramidPass.MipCountMax);

            Ids = new TextureIds[mipCount];
            Names = new string[mipCount];

            for (int i = 0; i < mipCount; i++)
            {
                string name = $"{k_BaseName}{i}";
                string texelName = $"{name}_TexelSize";

                Ids[i] = new TextureIds
                {
                    texture = Shader.PropertyToID(name),
                    texelSize = Shader.PropertyToID(texelName)
                };

                Names[i] = name;
            }
        }

        /// <summary>
        /// Returns the shader property IDs for the specified mip level.
        /// </summary>
        /// <param name="index">Mip index (0-based).</param>
        /// <returns>TextureIds for the mip level.</returns>
        public static TextureIds GetMipIds(int index)
        {
            if (Ids == null || index < 0 || index >= Ids.Length)
                throw new ArgumentOutOfRangeException(nameof(index), $"Mip index {index} is out of range (0..{Ids.Length - 1})");

            return Ids[index];
        }

        /// <summary>
        /// Returns the name of the specified mip level.
        /// </summary>
        /// <param name="index">Mip index (0-based).</param>
        /// <returns>Name string for the mip level.</returns>
        public static string GetMipName(int index)
        {
            if (Names == null || index < 0 || index >= Names.Length)
                throw new ArgumentOutOfRangeException(nameof(index), $"Mip index {index} is out of range (0..{Names.Length - 1})");

            return Names[index];
        }
    }
}
