#pragma once
 
#define _TAA_MotionVectorTexture        _MotionVectorTexture
#define sampler_TAA_MotionVectorTexture sampler_MotionVectorTexture

#if defined(_TaaJitter)
    #define _TAA_Jitter _TaaJitter
#elif defined(_TAA_Jitter)
    #define _TAA_Jitter _TAA_Jitter
#else
    #define _TAA_Jitter float2(0,0)
#endif

#if defined(_TaaJitterPrev)
    #define _TAA_JitterPrev _TaaJitterPrev
#elif defined(_TAA_JitterPrev)
    #define _TAA_JitterPrev _TAA_JitterPrev
#else
    #define _TAA_JitterPrev float2(0,0)
#endif

#define _TAA_DepthTexture               _CameraDepthTexture
#define sampler_TAA_DepthTexture        sampler_CameraDepthTexture