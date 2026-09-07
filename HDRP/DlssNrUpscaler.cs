#if ENABLE_UPSCALER_FRAMEWORK
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using RenderGraphBlitFilterMode = UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils.BlitFilterMode;
using RenderGraphTextureDesc = UnityEngine.Rendering.RenderGraphModule.TextureDesc;

namespace UnityRhi.DlssNr.Hdrp
{
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    internal static class DlssNrUpscalerRegistration
    {
        static DlssNrUpscalerRegistration() => Register();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeRuntime() => Register();

        private static void Register()
        {
            // Feature 18's combined 2x mode is not a valid HDRP RTHandle
            // upscaler path yet. Keep the implementation available for future
            // investigation, but do not expose it to HDRP as a selectable
            // IUpscaler. The supported pipeline is 1x NR followed by HDRP's
            // built-in DLSS Super Resolution pass.
            RenderPipelineManager.activeRenderPipelineTypeChanged -= DlssNrUpscaler.ReleaseResources;
            RenderPipelineManager.activeRenderPipelineTypeChanged += DlssNrUpscaler.ReleaseResources;
            Application.quitting -= DlssNrUpscaler.ReleaseResources;
            Application.quitting += DlssNrUpscaler.ReleaseResources;
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= DlssNrUpscaler.ReleaseResources;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DlssNrUpscaler.ReleaseResources;
#endif
        }
    }

    [Serializable]
    public sealed class DlssNrUpscalerOptions : UpscalerOptions
    {
        private void OnEnable()
        {
            upscalerName = DlssNrUpscaler.UpscalerName;
            injectionPoint = DynamicResolutionHandler.UpsamplerScheduleType.AfterPost;
        }
    }

    /// <summary>HDRP IUpscaler integration for Feature 18's fixed 2x mode.</summary>
    public sealed class DlssNrUpscaler : AbstractUpscaler
    {
        public const string UpscalerName = "DLSS Neural Rendering 2x";

        private sealed class CameraState
        {
            public Camera Camera;
            public DlssNrCameraContext Context;
            public int LastUsedFrame;
        }

        private sealed class PassData
        {
            public TextureHandle InputColor;
            public TextureHandle InputDepth;
            public TextureHandle InputMotion;
            public TextureHandle PreparedColor;
            public TextureHandle PreparedMotion;
            public TextureHandle PreparedDepth;
            public TextureHandle Output;
            public Material PrepareMaterial;
            public DlssNrCameraContext Context;
            public DlssNrCameraContext.DispatchParameters DispatchParameters;
            public int InputWidth;
            public int InputHeight;
            public int OutputWidth;
            public int OutputHeight;
        }

        private static readonly int InputColorId = UnityEngine.Shader.PropertyToID("_DlssNrInputColor");
        private static readonly int InputScaleId = UnityEngine.Shader.PropertyToID("_DlssNrInputScale");
        private static readonly int CameraDepthId = UnityEngine.Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int CameraMotionId = UnityEngine.Shader.PropertyToID("_CameraMotionVectorsTexture");
        private static readonly Dictionary<ulong, CameraState> CameraStates = new();
        private static readonly Dictionary<ulong, int> ScheduledFrames = new();
        private static readonly List<ulong> DeadCameraIds = new();

        private static Material s_PrepareMaterial;
        private static int s_ScheduledFrame = int.MinValue;
        private static bool s_WarnedInvalidRatio;
        private static bool s_WarnedUnavailable;
        private static bool s_WarnedMissingCamera;
        private static bool s_WarnedDebugMode;
        private static bool s_WarnedSetup;
        private static bool s_WarnedException;

        private readonly DlssNrUpscalerOptions _options;

        public DlssNrUpscaler(DlssNrUpscalerOptions options)
        {
            _options = options;
        }

        public override string name => UpscalerName;
        public override UpscalerOptions options => _options;
        public override bool isTemporal => true;
        public override bool supportsSharpening => false;
        public override bool supportsXR => false;

        public override void CalculateJitter(int frameIndex, out Vector2 jitter, out bool allowScaling)
        {
            // The experimental Feature 18 command ABI does not expose jitter yet.
            // Moving the projection without passing the same offset to native is
            // worse than running without sub-pixel jitter.
            jitter = Vector2.zero;
            allowScaling = false;
        }

        public override void NegotiatePreUpscaleResolution(ref Vector2Int preUpscaleResolution,
            Vector2Int postUpscaleResolution)
        {
            if (postUpscaleResolution.x <= 0 || postUpscaleResolution.y <= 0)
                return;

            preUpscaleResolution.x = Mathf.Max(1, (postUpscaleResolution.x + 1) / 2);
            preUpscaleResolution.y = Mathf.Max(1, (postUpscaleResolution.y + 1) / 2);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UpscalingIO io = frameData.Get<UpscalingIO>();
            int frame = Time.frameCount;
            if (s_ScheduledFrame != frame)
            {
                ScheduledFrames.Clear();
                s_ScheduledFrame = frame;
            }
            ScheduledFrames[io.cameraInstanceID] = frame;

            Vector2Int inputSize = io.preUpscaleResolution;
            Vector2Int outputSize = io.postUpscaleResolution;
            bool validTextures = io.cameraColor.IsValid() && io.cameraDepth.IsValid() &&
                io.motionVectorColor.IsValid();
            if (!validTextures || inputSize.x <= 0 || inputSize.y <= 0 ||
                outputSize.x <= 0 || outputSize.y <= 0)
                return;

            Camera camera = FindCamera(io.cameraInstanceID);
            DlssNrHdrpPostProcess volume = VolumeManager.instance.stack
                .GetComponent<DlssNrHdrpPostProcess>();
            bool explicitTwoTimesSetup = IsExplicitlyEnabledForCamera(camera);
            bool afterPostInjection = _options != null &&
                _options.injectionPoint == DynamicResolutionHandler.UpsamplerScheduleType.AfterPost;
            bool exactTwoTimes = outputSize.x == inputSize.x * 2 &&
                outputSize.y == inputSize.y * 2;
            bool nativeEligible = explicitTwoTimesSetup && afterPostInjection &&
                exactTwoTimes && camera != null &&
                camera.cameraType != CameraType.SceneView &&
                camera.cameraType != CameraType.Preview &&
                camera.cameraType != CameraType.Reflection &&
                !camera.stereoEnabled && io.numActiveViews == 1 &&
                volume != null && volume.IsActive() &&
                volume.debugMode.value == DlssNrDebugMode.Off &&
                RhiCore.IsD3D12Active && RhiCore.IsDlssNrAvailable &&
                EnsurePrepareMaterial();

            if (!exactTwoTimes && !s_WarnedInvalidRatio)
            {
                s_WarnedInvalidRatio = true;
                Debug.LogWarning($"[UnityRHI.DLSS-NR] Feature 18 upscaling requires exact 2x dimensions; " +
                    $"using bilinear fallback for {inputSize.x}x{inputSize.y} -> {outputSize.x}x{outputSize.y}.");
            }
            else if (camera == null && !s_WarnedMissingCamera)
            {
                s_WarnedMissingCamera = true;
                Debug.LogWarning("[UnityRHI.DLSS-NR] Could not resolve the HDRP camera for this upscaler pass; " +
                    "using the full-resolution bilinear fallback.");
            }
            else if (exactTwoTimes && volume != null && volume.IsActive() &&
                (!RhiCore.IsD3D12Active || !RhiCore.IsDlssNrAvailable) && !s_WarnedUnavailable)
            {
                s_WarnedUnavailable = true;
                Debug.LogWarning("[UnityRHI.DLSS-NR] Feature 18 is unavailable; " +
                    "using the full-resolution bilinear fallback.");
            }
            else if (exactTwoTimes && volume != null && volume.IsActive() &&
                volume.debugMode.value != DlssNrDebugMode.Off && !s_WarnedDebugMode)
            {
                s_WarnedDebugMode = true;
                Debug.LogWarning("[UnityRHI.DLSS-NR] Volume debug visualization is not available " +
                    "inside the 2x IUpscaler pass; using the full-resolution bilinear fallback.");
            }
            else if ((!explicitTwoTimesSetup || !afterPostInjection) && !s_WarnedSetup)
            {
                s_WarnedSetup = true;
                Debug.LogWarning("[UnityRHI.DLSS-NR] The experimental 2x path requires this upscaler " +
                    "at priority 1, Force Resolution, Camera Allow Dynamic Resolution, and " +
                    "its injection point set to After Post. Using the full-resolution bilinear fallback.");
            }

            if (!nativeEligible)
            {
                io.cameraColor = AddFallbackPass(renderGraph, io.cameraColor, outputSize);
                return;
            }

            DlssNrSettings settings = volume.GetSettingsSnapshot();
            DlssNrCameraContext context;
            try
            {
                PruneDeadCameras();
                context = GetOrCreateContext(io.cameraInstanceID, camera, inputSize, outputSize);
                if (io.resetHistory)
                    context.ResetHistory();
            }
            catch (Exception exception)
            {
                LogExceptionOnce("resource setup", exception);
                io.cameraColor = AddFallbackPass(renderGraph, io.cameraColor, outputSize);
                return;
            }

            TextureHandle preparedColor = renderGraph.ImportTexture(context.ColorHandle);
            TextureHandle preparedMotion = renderGraph.ImportTexture(context.MotionHandle);
            TextureHandle preparedDepth = renderGraph.ImportTexture(context.DepthHandle);
            SetFullSizeOutputProperties(context.OutputHandle, outputSize);
            TextureHandle output = renderGraph.ImportTexture(context.OutputHandle);

            // Always initialize the full output. If native Create/Evaluate fails,
            // its guarded failure path leaves this valid image untouched.
            renderGraph.AddBlitPass(io.cameraColor, output, Vector2.one, Vector2.zero,
                filterMode: RenderGraphBlitFilterMode.ClampBilinear,
                passName: "DLSS-NR Full-Resolution Fallback");

            using (var builder = renderGraph.AddUnsafePass<PassData>(
                "DLSS Neural Rendering 2x", out PassData passData,
                new ProfilingSampler("DLSS Neural Rendering 2x")))
            {
                builder.UseTexture(io.cameraColor, AccessFlags.Read);
                builder.UseTexture(io.cameraDepth, AccessFlags.Read);
                builder.UseTexture(io.motionVectorColor, AccessFlags.Read);
                builder.UseTexture(preparedColor, AccessFlags.Write);
                builder.UseTexture(preparedMotion, AccessFlags.Write);
                builder.UseTexture(preparedDepth, AccessFlags.Write);
                builder.UseTexture(output, AccessFlags.ReadWrite);

                passData.InputColor = io.cameraColor;
                passData.InputDepth = io.cameraDepth;
                passData.InputMotion = io.motionVectorColor;
                passData.PreparedColor = preparedColor;
                passData.PreparedMotion = preparedMotion;
                passData.PreparedDepth = preparedDepth;
                passData.Output = output;
                passData.PrepareMaterial = s_PrepareMaterial;
                passData.Context = context;
                passData.DispatchParameters = context.BeginFrame(camera, frame, settings);
                passData.InputWidth = inputSize.x;
                passData.InputHeight = inputSize.y;
                passData.OutputWidth = outputSize.x;
                passData.OutputHeight = outputSize.y;

                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext graphContext) =>
                {
                    CommandBuffer command = CommandBufferHelpers.GetNativeCommandBuffer(graphContext.cmd);
                    MaterialPropertyBlock properties = graphContext.renderGraphPool
                        .GetTempMaterialPropertyBlock();
                    RTHandle inputColor = data.InputColor;
                    Vector4 inputScale = inputColor.rtHandleProperties.rtHandleScale;
                    if (inputScale.x <= 0f || inputScale.y <= 0f)
                        inputScale = Vector4.one;

                    properties.SetTexture(InputColorId, data.InputColor);
                    properties.SetTexture(CameraDepthId, data.InputDepth);
                    properties.SetTexture(CameraMotionId, data.InputMotion);
                    properties.SetVector(InputScaleId, inputScale);

                    RenderTargetIdentifier[] targets =
                    {
                        data.Context.ColorRt,
                        data.Context.MotionRt,
                        data.Context.DepthRt,
                    };
                    command.SetRenderTarget(targets, BuiltinRenderTextureType.None);
                    command.SetViewport(new Rect(0f, 0f, data.InputWidth, data.InputHeight));
                    command.DrawProcedural(Matrix4x4.identity, data.PrepareMaterial, 0,
                        MeshTopology.Triangles, 3, 1, properties);

                    try
                    {
                        data.Context.Record(command, data.DispatchParameters);
                    }
                    catch (Exception exception)
                    {
                        data.Context.ResetHistory();
                        LogExceptionOnce("native command recording", exception);
                    }

                    // The preparation draw deliberately installs the low-resolution
                    // viewport. Unsafe RenderGraph passes do not restore it for the
                    // following FinalPost pass, so leave a full-output target and
                    // viewport bound after the native event.
                    command.SetRenderTarget(data.Context.OutputRt);
                    command.SetViewport(new Rect(0f, 0f, data.OutputWidth, data.OutputHeight));
                });
            }

            CameraStates[io.cameraInstanceID].LastUsedFrame = frame;
            DlssNrHdrpPostProcess.UpdateDiagnostics(camera, inputSize, outputSize);
            io.cameraColor = output;
        }

        internal static bool WasScheduledForCamera(Camera camera, int frame)
        {
            if (camera == null)
                return false;
            ulong id = EntityId.ToULong(camera.GetEntityId());
            return ScheduledFrames.TryGetValue(id, out int scheduledFrame) && scheduledFrame == frame;
        }

        internal static bool IsExplicitlyEnabledForCamera(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Game ||
                !camera.allowDynamicResolution)
                return false;

            HDRenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            if (asset == null)
                return false;

            GlobalDynamicResolutionSettings settings = asset
                .currentPlatformRenderPipelineSettings.dynamicResolutionSettings;
            return settings.enabled && settings.forceResolution &&
                settings.advancedUpscalerNames != null &&
                settings.advancedUpscalerNames.Count > 0 &&
                settings.advancedUpscalerNames[0] == UpscalerName;
        }

        internal static void ReleaseResources()
        {
            if (CameraStates.Count > 0 && RhiCore.IsD3D12Active)
            {
                try
                {
                    if (!RhiCore.WaitForGpuIdle())
                        Debug.LogError("[UnityRHI.DLSS-NR] GPU did not become idle before upscaler cleanup.");
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[UnityRHI.DLSS-NR] GPU idle wait failed during upscaler cleanup: {exception}");
                }
            }

            foreach (CameraState state in CameraStates.Values)
                state.Context?.Dispose();
            CameraStates.Clear();
            ScheduledFrames.Clear();
            s_ScheduledFrame = int.MinValue;
            DeadCameraIds.Clear();
            CoreUtils.Destroy(s_PrepareMaterial);
            s_PrepareMaterial = null;
        }

        private static Camera FindCamera(ulong cameraId)
        {
            if (CameraStates.TryGetValue(cameraId, out CameraState state) && state.Camera != null)
                return state.Camera;

            Camera[] cameras = Camera.allCameras;
            foreach (Camera camera in cameras)
            {
                if (camera != null && EntityId.ToULong(camera.GetEntityId()) == cameraId)
                    return camera;
            }

            return null;
        }

        private static DlssNrCameraContext GetOrCreateContext(ulong cameraId, Camera camera,
            Vector2Int inputSize, Vector2Int outputSize)
        {
            if (CameraStates.TryGetValue(cameraId, out CameraState state))
            {
                DlssNrCameraContext existing = state.Context;
                if (existing.Width == inputSize.x && existing.Height == inputSize.y &&
                    existing.OutputWidth == outputSize.x && existing.OutputHeight == outputSize.y)
                {
                    state.Camera = camera;
                    return existing;
                }

                DisposeContextSafely(existing, "upscaler resolution change");
            }

            var context = new DlssNrCameraContext(inputSize.x, inputSize.y,
                outputSize.x, outputSize.y, camera.name);
            CameraStates[cameraId] = new CameraState
            {
                Camera = camera,
                Context = context,
                LastUsedFrame = Time.frameCount,
            };
            return context;
        }

        private static void PruneDeadCameras()
        {
            DeadCameraIds.Clear();
            foreach (KeyValuePair<ulong, CameraState> pair in CameraStates)
            {
                if (pair.Value.Camera == null)
                    DeadCameraIds.Add(pair.Key);
            }

            foreach (ulong cameraId in DeadCameraIds)
            {
                DisposeContextSafely(CameraStates[cameraId].Context, "camera destruction");
                CameraStates.Remove(cameraId);
                ScheduledFrames.Remove(cameraId);
            }
            DeadCameraIds.Clear();
        }

        private static void DisposeContextSafely(DlssNrCameraContext context, string reason)
        {
            if (context == null)
                return;
            if (RhiCore.IsD3D12Active)
            {
                try
                {
                    if (!RhiCore.WaitForGpuIdle())
                        Debug.LogError($"[UnityRHI.DLSS-NR] GPU did not become idle before {reason}.");
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[UnityRHI.DLSS-NR] GPU idle wait failed before {reason}: {exception}");
                }
            }
            context.Dispose();
        }

        private static TextureHandle AddFallbackPass(RenderGraph renderGraph, TextureHandle input,
            Vector2Int outputSize)
        {
            RenderGraphTextureDesc outputDesc = input.GetDescriptor(renderGraph);
            outputDesc.width = outputSize.x;
            outputDesc.height = outputSize.y;
            outputDesc.msaaSamples = MSAASamples.None;
            outputDesc.useMipMap = false;
            outputDesc.autoGenerateMips = false;
            outputDesc.useDynamicScale = false;
            outputDesc.enableRandomWrite = false;
            outputDesc.clearBuffer = false;
            outputDesc.name = "DLSS-NR Bilinear Fallback";
            TextureHandle output = renderGraph.CreateTexture(outputDesc);
            renderGraph.AddBlitPass(input, output, Vector2.one, Vector2.zero,
                filterMode: RenderGraphBlitFilterMode.ClampBilinear,
                passName: "DLSS-NR Bilinear Fallback");
            return output;
        }

        private static void SetFullSizeOutputProperties(RTHandle handle, Vector2Int outputSize)
        {
            RTHandleProperties properties = handle.rtHandleProperties;
            properties.rtHandleScale = Vector4.one;
            properties.currentRenderTargetSize = outputSize;
            properties.previousRenderTargetSize = outputSize;
            properties.currentViewportSize = outputSize;
            properties.previousViewportSize = outputSize;
            handle.SetCustomHandleProperties(properties);
        }

        private static bool EnsurePrepareMaterial()
        {
            if (s_PrepareMaterial != null)
                return true;
            UnityEngine.Shader shader = UnityEngine.Shader.Find("Hidden/UnityRHI/DLSS-NR/PrepareInputs");
            if (shader == null)
                return false;
            s_PrepareMaterial = CoreUtils.CreateEngineMaterial(shader);
            return s_PrepareMaterial != null;
        }

        private static void LogExceptionOnce(string stage, Exception exception)
        {
            if (s_WarnedException)
                return;
            s_WarnedException = true;
            Debug.LogError($"[UnityRHI.DLSS-NR] Upscaler {stage} failed; " +
                $"the full-resolution fallback remains active. {exception}");
        }
    }
}
#endif
