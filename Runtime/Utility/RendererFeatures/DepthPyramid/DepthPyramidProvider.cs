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
        None,

        /// <summary> 
        /// Selects the smallest depth value (nearest point). 
        /// Useful for SSAO and Occlusion Culling. 
        /// </summary>
        Near,

        /// <summary> 
        /// Selects the largest depth value (farthest point). 
        /// Essential for Raymarching (Empty Space Skipping). 
        /// </summary>
        Far
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

        private static ChainData s_ChainNear = new ChainData { Suffix = "Near", Mips = Array.Empty<TextureHandleMeta<RTHandle>>() };
        private static ChainData s_ChainFar = new ChainData { Suffix = "Far", Mips = Array.Empty<TextureHandleMeta<RTHandle>>() };

        private static TextureHandleMeta<RTHandle> s_HistoryDepth;
        private static bool s_HistoryRequested;

        private const string k_BaseName = "_" + Globals.CompanyName + "_DepthPyramid";

        private static Vector2Int s_BaseResNear;
        private static Vector2Int s_BaseResFar;

        internal const uint NearDirty = 1 << 0;
        internal const uint FarDirty = 1 << 1;
        internal const uint AllDirty = NearDirty | FarDirty;

        private static DirtyFlags s_Dirty;

        /// <summary> 
        /// Returns true if the current graphics API uses Reversed-Z (Near=1.0, Far=0.0).
        /// Used to synchronize math logic between C# and Compute Shaders.
        /// </summary>
        public static bool IsReversedZ => s_IsReversedZ;
        private static bool s_IsReversedZ;

        static DepthPyramidProvider()
        {
            s_IsReversedZ = SystemInfo.usesReversedZBuffer;
            Shader.SetGlobalInt(k_BaseName + "_IsReversedZ", s_IsReversedZ ? 1 : 0);
        }

        internal static bool IsAnyDirty => s_Dirty.Any;

        /// <summary>
        /// Returns the dirty status for a specific semantic chain.
        /// </summary>
        internal static bool IsDirty(DepthChainType type)
        {
            return type switch
            {
                DepthChainType.Near => s_Dirty.IsDirty(NearDirty),
                DepthChainType.Far => s_Dirty.IsDirty(FarDirty),
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
                case DepthChainType.Near:
                    s_Dirty.Clear(NearDirty);
                    break;
                case DepthChainType.Far:
                    s_Dirty.Clear(FarDirty);
                    break;
            }
        }

        /// <summary>
        /// Returns true if any feature has requested a persistent depth history.
        /// Use this in your Render Pass to decide whether to perform a CopyTexture.
        /// </summary>
        public static bool IsHistoryRequested => s_HistoryRequested;

        /// <summary>
        /// Helper to access the current requested count for a specific chain.
        /// </summary>
        public static int GetRequestedCount(DepthChainType type)
        {
            return type switch
            {
                DepthChainType.Near => s_ChainNear.RequestedCount,
                DepthChainType.Far => s_ChainFar.RequestedCount,
                _ => 0
            };
        }

        /// <summary>
        /// Returns the base resolution for which the specific chain was last generated.
        /// </summary>
        public static Vector2Int GetBaseResolution(DepthChainType type)
        {
            return type switch
            {
                DepthChainType.Near => s_BaseResNear,
                DepthChainType.Far => s_BaseResFar,
                _ => Vector2Int.zero
            };
        }

        /// <summary>
        /// Private method to update the resolution. 
        /// This replaces the "private set" logic for static fields.
        /// </summary>
        private static void SetBaseResolution(DepthChainType type, Vector2Int res)
        {
            switch (type)
            {
                case DepthChainType.Near: s_BaseResNear = res; break;
                case DepthChainType.Far: s_BaseResFar = res; break;
            }
        }

        /// <summary>
        /// Public API to request specific depth chains. 
        /// The dirty flag is handled based on the provided DepthChainType.
        /// </summary>
        public static void EnsureMipCount(DepthChainType type, int count, bool force = false)
        {
            switch (type)
            {
                case DepthChainType.Near:
                    EnsureMipCount(ref s_ChainNear, count, NearDirty, force);
                    break;
                case DepthChainType.Far:
                    EnsureMipCount(ref s_ChainFar, count, FarDirty, force);
                    break;
            }
        }

        /// <summary>
        /// Completely resets the requested counts and metadata for a specific chain or all chains.
        /// Useful when a feature is disabled or the rendering context changes significantly.
        /// </summary>
        /// <param name="type">The chain to reset. Use 'None' to only reset history, or a specific chain type.</param>
        public static void DisableDepthChain(DepthChainType type = DepthChainType.None)
        {
            switch (type)
            {
                case DepthChainType.Near:
                    ResetChainData(ref s_ChainNear, NearDirty);
                    break;
                case DepthChainType.Far:
                    ResetChainData(ref s_ChainFar, FarDirty);
                    break;
            }
        }

        /// <summary>
        /// Requests that the depth history (full resolution) be maintained.
        /// </summary>
        public static void RequestDepthHistory()
        {
            if (!s_HistoryRequested)
            {
                s_HistoryRequested = true;
                s_Dirty.MarkDirty(AllDirty);
            }
        }

        /// <summary>
        /// Explicitly disables the depth history requirement.
        /// Should be called when features like TAA are turned off.
        /// </summary>
        public static void DisableDepthHistory()
        {
            s_HistoryRequested = false;
            s_HistoryDepth = default;
            s_Dirty.MarkDirty(AllDirty);
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
                DepthChainType.Near => s_ChainNear.Mips,
                DepthChainType.Far => s_ChainFar.Mips,
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
        /// Returns the metadata for the depth history texture.
        /// </summary>
        public static TextureHandleMeta<RTHandle> GetHistoryDepth() => s_HistoryDepth;

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
        /// Returns a reference to the actual chain data. 
        /// Necessary to modify the Mips array or RequestedCount directly.
        /// </summary>
        private static ref ChainData GetChainRef(DepthChainType type)
        {
            if (type == DepthChainType.Near) return ref s_ChainNear;
            if (type == DepthChainType.Far) return ref s_ChainFar;

            throw new ArgumentOutOfRangeException(nameof(type), "Cannot return ref to invalid chain type.");
        }

        /// <summary>
        /// Recreates the metadata array for a specific chain type based on the current requested count.
        /// Texture handles are initialized as null until the pass binds them.
        /// </summary>
        /// <param name="type">The specific depth chain to generate.</param>
        /// <param name="baseRes">Base resolution (usually the camera pixel rect).</param>
        internal static void GenerateChainMeta(DepthChainType type, Vector2Int baseRes)
        {
            switch (type)
            {
                case DepthChainType.Near:
                    GenerateChainMeta(type, baseRes, NearDirty);
                    break;
                case DepthChainType.Far:
                    GenerateChainMeta(type, baseRes, FarDirty);
                    break;
            }
        }

        /// <summary>
        /// Internal helper to initialize history metadata independently of mip chains.
        /// </summary>
        internal static void GenerateHistoryMeta(Vector2Int baseRes)
        {
            if (!s_HistoryRequested) return;

            string name = k_BaseName + "_History";

            var ids = new TextureIds
            {
                texture = Shader.PropertyToID(name),
                texelSize = Shader.PropertyToID($"{name}_TexelSize")
            };

            var texelSize = new Vector4(1f / baseRes.x, 1f / baseRes.y, (float)baseRes.x, (float)baseRes.y);

            s_HistoryDepth = new TextureHandleMeta<RTHandle>(ids, name, texelSize, null);
        }

        /// <summary>
        /// Internal helper that performs the actual array allocation and metadata calculation.
        /// </summary>
        private static void GenerateChainMeta(DepthChainType type, Vector2Int baseRes, uint flag)
        {
            var curBaseRes = GetBaseResolution(type);
            if (curBaseRes == baseRes && !s_Dirty.IsDirty(flag))
                return;

            SetBaseResolution(type, baseRes);

            ref ChainData chain = ref GetChainRef(type);
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
        /// Registers the physical <see cref="RTHandle"/> for the depth history (Frame N-1).
        /// This should be called after the History-Swap to ensure shaders and subsequent passes
        /// access the correct temporal data.
        /// </summary>
        /// <param name="history">The persistent RTHandle containing the previous frame's depth information.</param>
        internal static void SetHistoryDepth(RTHandle history)
            => s_HistoryDepth.Handle = history;

        /// <summary>
        /// Binds a specific RTHandleMipChain to the provider's metadata.
        /// </summary>
        /// <param name="type">The type of the chain being bound (Min, Max, or Point).</param>
        /// <param name="handleChain">The actual RTHandle chain containing the textures.</param>
        /// <param name="setGlobal">If true, sets the textures and texel sizes as global shader properties.</param>
        internal static void SetGlobalDepthPyramid(DepthChainType type, UnsafeRTHandleMipChain handleChain, bool setGlobal = false)
        {
            if (handleChain == null) return;

            switch (type)
            {
                case DepthChainType.Near:
                    BindChain(ref s_ChainNear, handleChain, setGlobal);
                    break;
                case DepthChainType.Far:
                    BindChain(ref s_ChainFar, handleChain, setGlobal);
                    break;
            }
        }

        /// <summary>
        /// Synchronizes RTHandles from a MipChain into the metadata structs.
        /// Optionally sets the handles as global shader properties (usually only for the Max/Main chain).
        /// </summary>
        private static void BindChain(ref ChainData chain, UnsafeRTHandleMipChain handleChain, bool setGlobal)
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

        /// <summary>
        /// Internal helper to wipe a specific chain's metadata and request status.
        /// </summary>
        private static void ResetChainData(ref ChainData chain, uint flag)
        {
            chain.RequestedCount = 0;
            chain.Mips = Array.Empty<TextureHandleMeta<RTHandle>>();

            s_Dirty.MarkDirty(flag);
        }
    }
}
