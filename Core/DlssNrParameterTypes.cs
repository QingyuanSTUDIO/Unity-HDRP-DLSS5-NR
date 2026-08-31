using System;
using UnityEngine.Rendering;

namespace UnityRhi.DlssNr.Hdrp
{
    public enum DlssNrDebugMode
    {
        Off,
        MotionVectors,
        MotionMagnitude,
        DeviceDepth,
        LinearEyeDepth,
    }

    [Serializable]
    public sealed class DlssNrPresetParameter : VolumeParameter<DlssNrPreset>
    {
        public DlssNrPresetParameter(DlssNrPreset value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    [Serializable]
    public sealed class DlssNrStyleParameter : VolumeParameter<DlssNrStyle>
    {
        public DlssNrStyleParameter(DlssNrStyle value, bool overrideState = false)
            : base(value, overrideState) { }
    }

    [Serializable]
    public sealed class DlssNrDebugModeParameter : VolumeParameter<DlssNrDebugMode>
    {
        public DlssNrDebugModeParameter(DlssNrDebugMode value, bool overrideState = false)
            : base(value, overrideState) { }
    }
}
