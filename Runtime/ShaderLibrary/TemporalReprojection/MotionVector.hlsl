#pragma once

/**
 * @brief Fallback: Decodes the motion vector from Unity's motion vector texture (NDC -> UV).
 * @param mv The raw motion vector sampled from _CameraMotionVectorsTexture in NDC.
 */
#if !defined(DecodeMotionVector)
float2 DecodeMotionVector(float2 mv)
{
    return mv * 0.5;
}
#endif