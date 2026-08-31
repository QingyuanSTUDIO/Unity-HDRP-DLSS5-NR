using UnityEngine;

namespace UnityRhi.DlssNr.Hdrp
{
    /// <summary>A per-camera snapshot of the blended DLSS-NR Volume values.</summary>
    public readonly struct DlssNrSettings
    {
        public readonly DlssNrPreset Preset;
        public readonly DlssNrStyle Style;
        public readonly float Intensity;
        public readonly float LocalToneStrength;
        public readonly float LocalStructureStrength;
        public readonly float SkinStructureStrength;
        public readonly bool UseAutoMask;
        public readonly bool UiCorrection;
        public readonly UnityEngine.Vector2 MotionVectorScale;
        public readonly float CameraCutDistance;
        public readonly float CameraCutAngle;
        public readonly DlssNrDebugMode DebugMode;
        public readonly float DebugMotionRange;
        public readonly float DebugDepthRange;

        public DlssNrSettings(DlssNrPreset preset, DlssNrStyle style, float intensity,
            float localToneStrength, float localStructureStrength, float skinStructureStrength,
            bool useAutoMask, bool uiCorrection, Vector2 motionVectorScale,
            float cameraCutDistance, float cameraCutAngle, DlssNrDebugMode debugMode,
            float debugMotionRange, float debugDepthRange)
        {
            Preset = preset;
            Style = style;
            Intensity = intensity;
            LocalToneStrength = localToneStrength;
            LocalStructureStrength = localStructureStrength;
            SkinStructureStrength = skinStructureStrength;
            UseAutoMask = useAutoMask;
            UiCorrection = uiCorrection;
            MotionVectorScale = motionVectorScale;
            CameraCutDistance = cameraCutDistance;
            CameraCutAngle = cameraCutAngle;
            DebugMode = debugMode;
            DebugMotionRange = debugMotionRange;
            DebugDepthRange = debugDepthRange;
        }
    }
}
