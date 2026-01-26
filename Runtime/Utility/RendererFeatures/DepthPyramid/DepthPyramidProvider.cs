using Rayforge.Core.Common;
using Rayforge.Core.Common.LowLevel;
using Rayforge.Core.Rendering.Collections.Helpers;
using Rayforge.Core.Rendering.Passes;
using Rayforge.Core.Utility.RenderGraphs.Collections;
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rayforge.URP.Utility.RendererFeatures.DepthPyramid
{
    /// <summary>
    /// Defines the downsampling logic for the different depth chains.
    /// </summary>
    public enum DepthChainType
    {
        /// <summary> 
        /// Selects the smallest depth value (nearest point). 
        /// Useful for SSAO and Occlusion Culling. 
        /// </summary>
        Min,

        /// <summary> 
        /// Selects the largest depth value (farthest point). 
        /// Essential for Raymarching (Empty Space Skipping). 
        /// </summary>
        Max
    }

    /// <summary>
    /// Central data provider for the Depth Pyramid. 
    /// Manages metadata and texture references synchronously within a single structure.
    /// </summary>
    public static class DepthPyramidProvider
    {
        /// <summary> Maximum number of supported mip levels. </summary>
        public const int MipCountMax = 16;

        private struct ChainData
        {
            public TextureHandleMeta<RTHandle>[] Mips;
            public string Suffix;
            public int RequestedCount;

            public int MipCount => Mips == null ? 0 : Mips.Length;
            public bool IsActive => Mips != null && Mips.Length > 0;
            public bool IsRequested => RequestedCount > 0;
        }

        private static ChainData s_ChainMin = new ChainData { Suffix = "Min", Mips = Array.Empty<TextureHandleMeta<RTHandle>>() };
        private static ChainData s_ChainMax = new ChainData { Suffix = "Max", Mips = Array.Empty<TextureHandleMeta<RTHandle>>() };

        private const string k_BaseName = "_" + Globals.CompanyName + "_DepthPyramid";

        private static Vector2Int s_CurrentBaseRes;

        internal const uint MinDirty = 1 << 0;
        internal const uint MaxDirty = 1 << 1;
        internal const uint AllDirty = MinDirty | MaxDirty;

        private static DirtyFlags s_Dirty;

        internal static bool IsAnyDirty => s_Dirty.Any;

        /// <summary>
        /// Checks if a specific chain type or any chain at all is marked as dirty.
        /// </summary>
        public static bool IsDirty(DepthChainType type)
        {
            return type switch
            {
                DepthChainType.Min => s_Dirty.IsDirty(MinDirty),
                DepthChainType.Max => s_Dirty.IsDirty(MaxDirty),
                _ => s_Dirty.Any
            };
        }

        /// <summary>
        /// Resets all dirty flags, marking all chains as up-to-date.
        /// </summary>
        internal static void ResetDirty() => s_Dirty.ClearAll();

        /// <summary>
        /// Resets the dirty flag for a specific chain type.
        /// Should be called after the respective chain has been regenerated and bound.
        /// </summary>
        /// <param name="type">The depth chain type to clear.</param>
        internal static void ResetDirty(DepthChainType type)
        {
            switch (type)
            {
                case DepthChainType.Min:
                    s_Dirty.Clear(MinDirty);
                    break;
                case DepthChainType.Max:
                    s_Dirty.Clear(MaxDirty);
                    break;
            }
        }

        /// <summary>
        /// Helper to access the current requested count for a specific chain.
        /// </summary>
        public static int GetRequestedCount(DepthChainType type)
        {
            return type switch
            {
                DepthChainType.Min => s_ChainMin.RequestedCount,
                DepthChainType.Max => s_ChainMax.RequestedCount,
                _ => 0
            };
        }

        /// <summary>
        /// Public API to request specific depth chains. 
        /// The dirty flag is handled based on the provided DepthChainType.
        /// </summary>
        public static void EnsureMipCount(DepthChainType type, int count, bool force = false)
        {
            switch (type)
            {
                case DepthChainType.Min:
                    EnsureMipCount(ref s_ChainMin, count, MinDirty, force);
                    break;
                case DepthChainType.Max:
                    EnsureMipCount(ref s_ChainMax, count, MaxDirty, force);
                    break;
            }
        }

        /// <summary>
        /// Internal method to handle array resizing and bitwise dirty flag updates.
        /// </summary>
        private static void EnsureMipCount(ref ChainData chain, int count, uint flag, bool force = false)
        {
            count = Math.Clamp(count, 0, MipCountMax);

            if (force || chain.RequestedCount < count)
            {
                if (chain.RequestedCount != count)
                {
                    chain.RequestedCount = count;
                    s_Dirty.MarkDirty(flag);
                }
            }
        }

        /// <summary>
        /// Returns the metadata for the requested chain as a ReadOnlySpan for high-performance iteration.
        /// </summary>
        /// <param name="type">The depth chain type (Min/Max).</param>
        /// <returns>A read-only view of the mip metadata array.</returns>
        public static ReadOnlySpan<TextureHandleMeta<RTHandle>> GetMips(DepthChainType type)
        {
            return type switch
            {
                DepthChainType.Min => s_ChainMin.Mips,
                DepthChainType.Max => s_ChainMax.Mips,
                _ => throw new ArgumentOutOfRangeException(nameof(type), "Unsupported depth chain type.")
            };
        }

        /// <summary>
        /// Returns the metadata for a specific mip level of the requested chain.
        /// </summary>
        /// <param name="type">The depth chain type (Min/Max).</param>
        /// <param name="index">The mip level index.</param>
        public static TextureHandleMeta<RTHandle> GetMip(DepthChainType type, int index)
        {
            var mips = GetMips(type);

            if (index < 0 || index >= mips.Length)
            {
                return default;
            }

            return mips[index];
        }

        /// <summary>
        /// Recreates the metadata array for a specific chain type based on the current requested count.
        /// Texture handles are initialized as null until the pass binds them.
        /// </summary>
        /// <param name="type">The specific depth chain to generate.</param>
        /// <param name="baseRes">Base resolution (usually the camera pixel rect).</param>
        internal static void Generate(DepthChainType type, Vector2Int baseRes)
        {
            switch (type)
            {
                case DepthChainType.Min:
                    GenerateChain(ref s_ChainMin, baseRes, MinDirty);
                    break;
                case DepthChainType.Max:
                    GenerateChain(ref s_ChainMax, baseRes, MaxDirty);
                    break;
            }
        }

        /// <summary>
        /// Internal helper that performs the actual array allocation and metadata calculation.
        /// </summary>
        private static void GenerateChain(ref ChainData chain, Vector2Int baseRes, uint flag)
        {
            if (s_CurrentBaseRes == baseRes && !s_Dirty.IsDirty(flag))
                return;

            s_CurrentBaseRes = baseRes;

            Array.Resize(ref chain.Mips, chain.RequestedCount);

            if (!chain.IsRequested) return;

            for (int i = 0; i < chain.RequestedCount; i++)
            {
                string name = $"{k_BaseName}{chain.Suffix}_Mip{i}";
                Vector2Int mipRes = MipChainHelpers.DefaultMipResolution(i, baseRes);

                var ids = new TextureIds
                {
                    texture = Shader.PropertyToID(name),
                    texelSize = Shader.PropertyToID($"{name}_TexelSize")
                };

                var texelSize = new Vector4(1f / mipRes.x, 1f / mipRes.y, (float)mipRes.x, (float)mipRes.y);

                chain.Mips[i] = new TextureHandleMeta<RTHandle>(ids, name, texelSize, null);
            }
        }

        /// <summary>
        /// Binds a specific RTHandleMipChain to the provider's metadata.
        /// </summary>
        /// <param name="type">The type of the chain being bound (Min, Max, or Point).</param>
        /// <param name="handleChain">The actual RTHandle chain containing the textures.</param>
        /// <param name="setGlobal">If true, sets the textures and texel sizes as global shader properties.</param>
        internal static void SetGlobalDepthPyramid(DepthChainType type, RTHandleMipChain handleChain, bool setGlobal = false)
        {
            if (handleChain == null) return;

            switch (type)
            {
                case DepthChainType.Max:
                    BindChain(ref s_ChainMax, handleChain, setGlobal);
                    break;
                case DepthChainType.Min:
                    BindChain(ref s_ChainMin, handleChain, setGlobal);
                    break;
            }
        }

        /// <summary>
        /// Synchronizes RTHandles from a MipChain into the metadata structs.
        /// Optionally sets the handles as global shader properties (usually only for the Max/Main chain).
        /// </summary>
        private static void BindChain(ref ChainData chain, RTHandleMipChain handleChain, bool setGlobal)
        {
            if (!chain.IsRequested || handleChain == null) return;

            int count = Math.Min(chain.MipCount, handleChain.MipCount);

            for (int i = 0; i < count; i++)
            {
                RTHandle currentHandle = handleChain[i];
                var m = chain.Mips[i];

                chain.Mips[i] = new TextureHandleMeta<RTHandle>(m.Meta, currentHandle);

                if (setGlobal)
                {
                    Shader.SetGlobalVector(m.Meta.Ids.texelSize, m.Meta.TexelSize);
                    if (currentHandle != null)
                    {
                        Shader.SetGlobalTexture(m.Meta.Ids.texture, currentHandle);
                    }
                }
            }
        }
    }
}
