# HDRP setup

This package exposes `DlssNrHdrpPostProcess` as an HDRP Custom Post Process
Volume component. It is a normal HDRP Volume override and does not use a
Renderer Feature or Custom Pass. The package contains no URP implementation.

1. Ensure HDRP 17+ and the UnityRHI native package are installed, and run on
   Windows/D3D12 with a supported NVIDIA driver.
2. Open **Edit > Project Settings > Graphics > HDRP Global Settings** and add
   `UnityRhi.DlssNr.Hdrp.DlssNrHdrpPostProcess` to **Custom Post Process Orders**
   under **After Post Process**.
3. In a Volume Profile choose **Add Override > Post-processing > DLSS Neural
   Rendering** and enable the `Enabled` override.

The component consumes HDRP's camera color, depth, and motion-vector textures.
It bypasses safely when HDRP has not produced depth or motion vectors for the
current camera, or when the native DLSS-NR runtime is unavailable.

The current HDRP implementation is intentionally limited to mono 1x native
rendering (`InputWidth == OutputWidth`, `Upscaling = false`). SceneView remains
pass-through by design. Stereo/XR cameras and invalid or unavailable targets
bypass to the original HDRP image. Persistent camera resources wait for GPU
idle before release when a camera is destroyed, resized, or the post process is
cleaned up.
