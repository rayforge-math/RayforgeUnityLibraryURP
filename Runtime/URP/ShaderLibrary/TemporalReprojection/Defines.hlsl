#pragma once
 
#define _TAA_MotionVectorTexture        _MotionVectorTexture
#define sampler_TAA_MotionVectorTexture sampler_MotionVectorTexture

#if !defined(_TaaJitter)
#define _TAA_Jitter                 _TAA_Jitter
#else
#define _TAA_Jitter                 _TaaJitter
#endif

#if !defined(_TaaJitterPrev)
#define _TAA_JitterPrev             _TAA_JitterPrev
#else
#define _TAA_JitterPrev             _TaaJitterPrev
#endif

#define _TAA_DepthTexture               _CameraDepthTexture
#define sampler_TAA_DepthTexture        sampler_CameraDepthTexture