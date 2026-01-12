#pragma once
 
#define _TAA_MotionVectorTexture        _MotionVectorTexture
#define sampler_TAA_MotionVectorTexture sampler_MotionVectorTexture

#ifdef _TaaJitter
    #define _TAA_Jitter _TaaJitter
#else
    #define _TAA_Jitter float2(0.0, 0.0)
#endif

#ifdef _TaaJitterPrev
    #define _TAA_JitterPrev _TaaJitterPrev
#else
    #define _TAA_JitterPrev float2(0.0, 0.0)
#endif

#define _TAA_DepthTexture               _CameraDepthTexture
#define sampler_TAA_DepthTexture        sampler_CameraDepthTexture