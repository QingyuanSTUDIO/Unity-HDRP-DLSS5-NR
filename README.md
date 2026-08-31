# Unity HDRP DLSS-NR

An HDRP-native integration of NVIDIA DLSS Neural Rendering for Unity 6. It is
implemented as a regular HDRP Volume custom post-process and does not require a
Renderer Feature or a Custom Pass.

## What It Does

The effect processes the current HDRP camera image at the same resolution. It
uses the raster camera color, depth, and motion-vector buffers, then sends them
to the UnityRHI DLSS-NR runtime. The result is written back into HDRP's post-
process chain.

This implementation is intended for neural image enhancement and temporal
reconstruction. It is not DLSS Super Resolution, Frame Generation, or Ray
Reconstruction.

## Requirements

- Unity 6.3 or newer
- HDRP 17 or newer
- RenderGraph-enabled HDRP
- Windows x64 with Direct3D 12
- A supported NVIDIA GPU and driver
- The `top.kuanmi.unityrhi` runtime package
- The matching embedded `top.kuanmi.unityrhi.native` package

The native package must be present under the project's `Packages` directory so
its D3D12/NGX initialization runs before the graphics device is created.

## Installation

1. Copy this repository's `Core`, `HDRP`, and `Shaders` folders into the Unity
   project's `Assets/Plugins/DLSS 5` directory.
2. Install the UnityRHI managed and native packages in `Packages`.
3. Set the graphics API to Direct3D 12 and restart the Unity Editor.
4. Open **Edit > Project Settings > Graphics > HDRP Global Settings**.
5. Add `UnityRhi.DlssNr.Hdrp.DlssNrHdrpPostProcess` to **Custom Post Process
   Orders > After Post Process**.
6. Create or select a Volume Profile and choose **Add Override > Post-processing
   > DLSS Neural Rendering**.
7. Enable the component and its **Enabled** override.

The camera must have HDRP depth and motion vectors enabled. No dynamic
resolution setting is required; the current implementation uses a 1:1 output
size.

## Volume Parameters

- **Preset** and **Style** select the neural rendering configuration.
- **Intensity**, **Local Tone Strength**, and **Local Structure Strength** control
  the enhancement strength.
- **Skin Structure Strength** adjusts the skin-detail response.
- **Motion Vector Scale** controls motion-vector conversion to pixels.
- **Camera Cut Distance** and **Camera Cut Angle** trigger temporal history reset.
- **Use Auto Mask** and **UI Correction** are forwarded to DLSS-NR when enabled.
- **Debug Mode** can visualize motion vectors, motion magnitude, device depth, or
  linear eye depth before native evaluation.

## Camera Behavior

Game cameras use the full DLSS-NR path. SceneView is passed through to the
original HDRP image because editor cameras do not provide a stable runtime
temporal history and may produce invalid or gray output with feature 18.

Each camera owns an independent native context and temporal history. Changing
resolution, camera cuts, projection, or Volume settings resets that history.

## Current Input Contract

The native dispatch receives:

```text
Color
Depth
MotionVectors
InputWidth / InputHeight
OutputWidth / OutputHeight
MotionVectorScaleX / MotionVectorScaleY
DepthInverted
Reset
Preset / Style
Intensity and structure parameters
```

Normals, roughness, albedo, reactive masks, exposure textures, and ray-tracing
buffers are not part of this DLSS-NR path.

## Troubleshooting

### Black or gray output

- Confirm the project is using Direct3D 12, not Direct3D 11.
- Confirm the native UnityRHI package is embedded under `Packages`.
- Confirm the custom post-process type is registered under **After Post Process**.
- Confirm the Volume is active and the `Enabled` override is checked.
- Set **Debug Mode** to inspect the prepared depth and motion-vector inputs.
- Disable the effect for SceneView; SceneView is intentionally pass-through.

### Image appears cropped or zoomed

Check the Game View aspect ratio and HDRP camera viewport. The integration uses
HDRP's actual camera dimensions and RTHandle scale; do not replace those values
with the backing texture dimensions.

## License

This repository contains the Unity integration layer. NVIDIA DLSS/NGX runtime
components remain subject to NVIDIA's license and redistribution terms.
