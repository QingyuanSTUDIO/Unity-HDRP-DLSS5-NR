#if ENABLE_UPSCALER_FRAMEWORK
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityRhi.Dlss.Hdrp
{
    [Serializable]
    public sealed class DlssUpscalerOptions : UpscalerOptions
    {
        [Tooltip("NATIVE runs DLAA at output resolution.")]
        public UpscalerMode qualityMode = UpscalerMode.QUALITY;
        [Tooltip("Use NGX's recommended input resolution. Disable to use HDRP Dynamic Resolution.")]
        public bool fixedResolutionMode = true;
        public DlssPreset preset = DlssPreset.Default;

        private void OnEnable()
        {
            upscalerName = DlssUpscaler.UpscalerName;
            injectionPoint = DynamicResolutionHandler.UpsamplerScheduleType.AfterPost;
        }

        public static UpscalerMode Sanitize(UpscalerMode mode) => mode switch
        {
            UpscalerMode.NATIVE => mode,
            UpscalerMode.QUALITY => mode,
            UpscalerMode.BALANCED => mode,
            UpscalerMode.PERFORMANCE => mode,
            UpscalerMode.ULTRA_PERFORMANCE => mode,
            _ => UpscalerMode.QUALITY,
        };

        public static float FallbackScale(UpscalerMode mode) => Sanitize(mode) switch
        {
            UpscalerMode.NATIVE => 1f,
            UpscalerMode.BALANCED => 0.58f,
            UpscalerMode.PERFORMANCE => 0.5f,
            UpscalerMode.ULTRA_PERFORMANCE => 0.33f,
            _ => 0.67f,
        };
    }
}
#endif
