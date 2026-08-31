using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityRhi.DlssNr.Hdrp
{
    /// <summary>HDRP-native DLSS-NR post process driven by a Volume override.</summary>
    [Serializable, VolumeComponentMenu("Post-processing/DLSS Neural Rendering")]
    public sealed class DlssNrHdrpPostProcess : CustomPostProcessVolumeComponent, IPostProcessComponent
    {
        public BoolParameter enabled = new(false);
        public DlssNrPresetParameter preset = new(DlssNrPreset.Default);
        public DlssNrStyleParameter style = new(DlssNrStyle.Default);
        public ClampedFloatParameter intensity = new(1f, 0f, 2f);
        public ClampedFloatParameter localToneStrength = new(1f, 0f, 2f);
        public ClampedFloatParameter localStructureStrength = new(1f, 0f, 2f);
        public ClampedFloatParameter skinStructureStrength = new(-1f, -1f, 2f);
        public BoolParameter useAutoMask = new(false);
        public BoolParameter uiCorrection = new(false);
        public Vector2Parameter motionVectorScale = new(Vector2.one);
        public MinFloatParameter cameraCutDistance = new(5f, 0f);
        public ClampedFloatParameter cameraCutAngle = new(45f, 0f, 180f);
        public DlssNrDebugModeParameter debugMode = new(DlssNrDebugMode.Off);
        public ClampedFloatParameter debugMotionRange = new(32f, 1f, 256f);
        public MinFloatParameter debugDepthRange = new(100f, 0.01f);

        private static readonly int InputColorId = UnityEngine.Shader.PropertyToID("_DlssNrInputColor");
        private static readonly int InputScaleId = UnityEngine.Shader.PropertyToID("_DlssNrInputScale");
        private static readonly int InputDepthId = UnityEngine.Shader.PropertyToID("_DlssNrInputDepth");
        private static readonly int InputMotionId = UnityEngine.Shader.PropertyToID("_DlssNrInputMotion");
        private static readonly int DebugModeId = UnityEngine.Shader.PropertyToID("_DlssNrDebugMode");
        private static readonly int DebugMotionScaleXId = UnityEngine.Shader.PropertyToID("_DlssNrDebugMotionScaleX");
        private static readonly int DebugMotionScaleYId = UnityEngine.Shader.PropertyToID("_DlssNrDebugMotionScaleY");
        private static readonly int DebugMotionRangeId = UnityEngine.Shader.PropertyToID("_DlssNrDebugMotionRange");
        private static readonly int DebugDepthRangeId = UnityEngine.Shader.PropertyToID("_DlssNrDebugDepthRange");

        private Material _prepareMaterial;
        private Material _debugMaterial;
        private readonly Dictionary<Camera, DlssNrCameraContext> _contexts = new();
        private bool _warned;

        public override CustomPostProcessInjectionPoint injectionPoint => CustomPostProcessInjectionPoint.AfterPostProcess;

        // Keep the component visible in the Volume inspector for SceneView. The
        // editor camera itself is passed through in Render because its temporal
        // buffers are not a valid DLSS-NR runtime input contract.
        public override bool visibleInSceneView => true;

        public override void Setup()
        {
            UnityEngine.Shader shader = UnityEngine.Shader.Find("Hidden/UnityRHI/DLSS-NR/PrepareInputs");
            if (shader != null)
            {
                _prepareMaterial = CoreUtils.CreateEngineMaterial(shader);
                _debugMaterial = CoreUtils.CreateEngineMaterial(shader);
            }
        }

        public bool IsActive() => active && enabled.value;
        public bool IsTileCompatible() => false;

        public override void Render(CommandBuffer cmd, HDCamera camera, RTHandle source, RTHandle destination)
        {
            if (!IsActive() || _prepareMaterial == null ||
                !RhiCore.IsD3D12Active || !RhiCore.IsDlssNrAvailable || camera == null || camera.camera == null ||
                camera.camera.cameraType == CameraType.Preview || camera.camera.cameraType == CameraType.Reflection ||
                source == null || source.rt == null || destination == null || destination.rt == null)
            {
                if (source != null && destination != null)
                    HDUtils.BlitCameraTexture(cmd, source, destination);
                return;
            }

            // SceneView has editor-only depth/motion resources and no stable
            // frame-to-frame presentation history. Running feature 18 here can
            // produce a gray output even though the debug passes render correctly.
            // Keep the editor view visible; Game cameras still use full DLSS-NR.
            if (camera.camera.cameraType == CameraType.SceneView)
            {
                HDUtils.BlitCameraTexture(cmd, source, destination);
                return;
            }

            // In HDRP custom post processes, the source RTHandle can carry a
            // backing-resource viewport (for example 1920x1920) that is not the
            // camera image size (for example 1920x1080). DLSS dimensions must use
            // HDCamera's actual viewport; the RTHandle scale below handles sampling
            // from any larger backing allocation.
            int width = camera.actualWidth;
            int height = camera.actualHeight;
            if ((width <= 0 || height <= 0) && source.rtHandleProperties.currentViewportSize.x > 0)
            {
                Vector2Int viewport = source.rtHandleProperties.currentViewportSize;
                width = viewport.x;
                height = viewport.y;
            }
            if ((width <= 0 || height <= 0) && source.rt != null)
            {
                width = source.rt.width;
                height = source.rt.height;
            }
            if (width <= 0 || height <= 0)
            {
                HDUtils.BlitCameraTexture(cmd, source, destination);
                return;
            }

            DlssNrCameraContext context;
            try
            {
                if (!_contexts.TryGetValue(camera.camera, out context) || context.Width != width || context.Height != height)
                {
                    context?.Dispose();
                    context = new DlssNrCameraContext(width, height, camera.camera.name);
                    _contexts[camera.camera] = context;
                }

                _prepareMaterial.SetTexture(InputColorId, source.rt);
                // Keep HDRP's RTHandle scale. In portrait/rotated Game views the
                // backing allocation and active viewport can have different axes;
                // recomputing width/height ratios here causes a zoomed/cropped input.
                Vector4 inputScale = source.rtHandleProperties.rtHandleScale;
                if (inputScale.x <= 0f || inputScale.y <= 0f)
                    inputScale = Vector4.one;
                _prepareMaterial.SetVector(InputScaleId, inputScale);
                RenderTargetIdentifier[] mrt = { context.ColorRt, context.MotionRt, context.DepthRt, context.OutputRt };
                cmd.SetRenderTarget(mrt, BuiltinRenderTextureType.None);
                // Custom post-process targets are persistent fixed-size textures,
                // while HDRP may leave a camera-scaled viewport active on the
                // command buffer. Set the full resource viewport explicitly so the
                // preparation pass covers the Game camera as well as SceneView.
                cmd.SetViewport(new Rect(0f, 0f, width, height));
                cmd.DrawProcedural(Matrix4x4.identity, _prepareMaterial, 0, MeshTopology.Triangles, 3, 1);

                DlssNrSettings settings = new DlssNrSettings(preset.value, style.value, intensity.value,
                    localToneStrength.value, localStructureStrength.value, skinStructureStrength.value,
                    useAutoMask.value, uiCorrection.value, motionVectorScale.value,
                    cameraCutDistance.value, cameraCutAngle.value, debugMode.value,
                    debugMotionRange.value, debugDepthRange.value);
                if (settings.DebugMode != DlssNrDebugMode.Off)
                {
                    _debugMaterial.SetTexture(InputDepthId, context.DepthRt);
                    _debugMaterial.SetTexture(InputMotionId, context.MotionRt);
                    _debugMaterial.SetInt(DebugModeId, (int)settings.DebugMode);
                    _debugMaterial.SetFloat(DebugMotionScaleXId, -width * settings.MotionVectorScale.x);
                    _debugMaterial.SetFloat(DebugMotionScaleYId, -height * settings.MotionVectorScale.y);
                    _debugMaterial.SetFloat(DebugMotionRangeId, settings.DebugMotionRange);
                    _debugMaterial.SetFloat(DebugDepthRangeId, settings.DebugDepthRange);
                    cmd.SetRenderTarget(context.OutputRt);
                    cmd.SetViewport(new Rect(0f, 0f, width, height));
                    cmd.DrawProcedural(Matrix4x4.identity, _debugMaterial, 1, MeshTopology.Triangles, 3, 1);
                }
                else
                {
                    context.Record(cmd, context.BeginFrame(camera.camera, Time.frameCount, settings));
                }

                // The persistent output texture is exactly the DLSS viewport size,
                // so override its handle properties for this blit. Otherwise HDRP's
                // pooled reference/rotation scale can make the result appear zoomed.
                RTHandleProperties outputProps = context.OutputHandle.rtHandleProperties;
                outputProps.rtHandleScale = Vector4.one;
                outputProps.currentRenderTargetSize = new Vector2Int(width, height);
                outputProps.previousRenderTargetSize = new Vector2Int(width, height);
                outputProps.currentViewportSize = new Vector2Int(width, height);
                context.OutputHandle.SetCustomHandleProperties(outputProps);
                try
                {
                    HDUtils.BlitCameraTexture2D(cmd, context.OutputHandle, destination);
                }
                finally
                {
                    context.OutputHandle.ClearCustomHandleProperties();
                }
            }
            catch (Exception exception)
            {
                if (!_warned) { _warned = true; Debug.LogError($"[UnityRHI.DLSS-NR] HDRP post process failed: {exception}"); }
                HDUtils.BlitCameraTexture(cmd, source, destination);
            }
        }

        public override void Cleanup()
        {
            foreach (DlssNrCameraContext context in _contexts.Values) context.Dispose();
            _contexts.Clear();
            CoreUtils.Destroy(_prepareMaterial);
            CoreUtils.Destroy(_debugMaterial);
            _prepareMaterial = null;
            _debugMaterial = null;
        }
    }
}
