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

The default setup remains the validated mono 1x path (`InputWidth ==
OutputWidth`, `Upscaling = false`). SceneView remains pass-through by design.
Stereo/XR cameras and invalid or unavailable targets bypass to the original
HDRP image. Persistent camera resources wait for GPU idle before release when a
camera is destroyed, resized, or the post process is cleaned up.

## DLSS Super Resolution

The `UnityRHI DLSS` IUpscaler uses the same native NGX Super Resolution
backend as the URP implementation. It is independent of the NR Volume and
supports DLAA (`NATIVE`), Quality, Balanced, Performance and Ultra Performance.
This integration targets Unity 6000.5 / HDRP 17.5, Windows D3D12 and mono Game
cameras.

1. Enable `ENABLE_UPSCALER_FRAMEWORK` in Player Settings.
2. In the active HDRP Asset, enable **Dynamic Resolution**, use **Software**
   mode and enable **Force Screen Percentage** for fixed quality ratios.
   Set **Default Fallback Upscale Filter** to **Catmull-Rom**. In HDRP 17.5,
   TAA Upscale can take precedence over an external IUpscaler and prevent SR
   from executing.
3. Put `UnityRHI DLSS` first in **Advanced Upscalers by Priority**. Selecting
   the HDRP Asset creates its `DlssUpscalerOptions` sub-asset automatically.
4. Select the quality and NGX preset in those options. Leave
   **Fixed Resolution Mode** enabled to use NGX's recommended input size.
   **Forced Percentage** does not override this negotiated quality ratio.
5. Enable **HDRP Dynamic Resolution** on the Game camera and enable motion
   vectors in its effective Frame Settings. The separate camera **Allow DLSS**
   checkbox controls Unity's built-in implementation; this module does not
   use that checkbox or add another camera Inspector section.
6. Keep the SR injection point at **After Post**. Enable camera post processing
   and use SDR display output. The SR instance consumes post-tonemap SDR color,
   even though HDRP stores that color in a floating-point texture.
7. Install UnityRHI native API 11 or newer. For this project's prepared build,
   save the scene, close its Unity Editor, run `Install-UnityRHI-SDR.cmd` in the
   project root, then reopen the project. The installer verifies the build hash
   and backs up the previous DLL. C# script reload alone cannot replace a loaded
   native DLL. API 10 temporarily uses spatial upscaling with zero SR jitter.

If the add menu only shows DLSS/STP after a script reload, display the Game or
Scene view once so HDRP can recreate its pipeline, then reopen the menu. The
HDRP Inspector lists external upscalers from the live pipeline.

The order is **low-resolution HDR rendering -> HDRP post processing / tone
mapping -> NR After Post Process -> SDR DLSS SR -> final output**. HDRP 17.5
executes its AfterPostProcess custom Volume list before its AfterPost IUpscaler.
The NR Volume is optional and must remain registered in that Volume list; SR
does not invoke a second NR pass. Other custom effects in the list also feed SR.

With Performance mode at 3840x2160, the intended sizes are NR at 1920x1080
and SR at 1920x1080 -> 3840x2160. NR color, depth and motion therefore use
the render viewport instead of a 4K color / 1080p auxiliary combination. NR
remains a 1x feature; SR performs the enlargement. Native mode still runs both
at output size. The native SDR feature omits HDR and automatic exposure flags;
the original parameterless `DlssContext` API preserves the URP HDR path.

The SR pass supports mono 2D inputs and HDRP single-layer texture arrays. It
copies the active color/depth/motion viewport into fixed-size native
textures, uses HDRP's actual camera jitter, and restores the original alpha
after NGX. Each camera owns its history. Frame discontinuities, camera cuts,
projection changes, quality/preset changes, resizes and HDRP resets invalidate
that history. Resizing or changing quality waits for the GPU before releasing
the old NGX feature and may cause a short stall.

SR resolves native RGB and source alpha through `UnityRhiDlssResolve.compute`
into either a mono 2D texture or HDRP's single-layer texture array. Both SR
and NR submit a sync-point event after native dispatch: the current native
backend clears D3D12 graphics state, and Unity 6000.5 does not restore all of
it for later draws on that command list. This submission boundary restores
subsequent HDRP rendering. There is no per-frame CPU wait for GPU completion,
but the extra submissions have a performance cost that has not been measured.

Unavailable NGX or unsupported inputs use spatial upscaling. The output is also
prefilled before native dispatch to cover unequal-size native failures.
XR/stereo is not supported by this SR implementation; select an XR-capable
upscaler for those cameras. SceneView does not run NGX SR.

`DlssUpscaler.LastInputSize`, `LastOutputSize` and `RecordedFrames` report
managed pass activity. `RhiCore.DlssLastCreateResult` and
`DlssLastEvaluateResult` report asynchronous native results; NGX success is
`0x00000001`. Neither recorded frames nor NGX success alone prove that the
resolved HDRP target or final Game view contains a valid image.

The SDR path uses already-exposed post-process color and does not apply HDRP
pre-exposure a second time. NR now receives color before SR's temporal resolve;
NR/SR behavior with jittered input and moving scenes still needs visual
validation. Motion blur and image-warping effects can change color without
matching changes to depth/motion. The existing native backend also has an unvalidated
same-size failure-copy path used by DLAA; GPU success tests do not validate
that error path.

Previously validated on Unity 6000.5.10f1 / HDRP 17.5 / RTX 4090: shader passes, mono
texture-array inputs, motion sentinel decoding, alpha preservation, NR input
mapping after SR, history wrap/reset, Performance SR and DLAA native GPU
evaluation. After the graphics-state handoff fix, the MainCamera's 2560x1440
SR resolve was read back: all 3,686,400 pixels were nonblack, RGB matched the
native output, and source alpha was restored. The Game view was also verified
at 3840x2160, with SR running at 1920x1080 -> 3840x2160 and NR at 3840x2160;
both reported successful NGX Create/Evaluate results. Moving-scene quality,
exposure behavior and performance still require manual testing. Those GPU
checks used the earlier SR-before-NR order, not the new SDR path.

AfterPost update: native API 11 and both managed assemblies compile. Unity
has confirmed AfterPost scheduling, NR at 1920x1080 and zero jitter in the
API-10 spatial fallback. After installing the new DLL and reopening the project,
both NR (1920x1080 -> 1920x1080) and SDR SR (1920x1080 -> 3840x2160) reported
NGX Create/Evaluate success. GPU readback confirmed SR input exactly matches
NR output after HDRP's B10G11R11_UFloatPack32 intermediate conversion. All
8,294,400 resolved 4K pixels were nonblack and finite, and resolved RGB exactly
matched native SR output. Play-mode movement quality and performance still
require manual testing.

## Experimental NR 2x code

`DlssNrUpscaler.cs` contains an earlier NR 2x experiment. Its registration is
deliberately disabled, so `DLSS Neural Rendering 2x` is not an available
upscaler. Use `UnityRHI DLSS` for Super Resolution and the existing NR Volume
for Neural Rendering.
