#if ENABLE_UPSCALER_FRAMEWORK
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.RenderGraphModule;
using RgTextureDesc = UnityEngine.Rendering.RenderGraphModule.TextureDesc;
using UnityShader = UnityEngine.Shader;

namespace UnityRhi.Dlss.Hdrp
{
    /// <summary>Mono HDRP integration for UnityRHI's NGX Super Resolution backend.</summary>
    public sealed class DlssUpscaler : AbstractUpscaler, IDisposable
    {
        public const string UpscalerName = "UnityRHI DLSS";

        private sealed class PassData
        {
            public TextureHandle Color, Depth, Motion, Destination;
            public Material Material;
            public ComputeShader ResolveShader;
            public int ResolveKernel;
            public DlssCameraContext Context;
            public Vector2 Jitter, MotionScale;
            public bool Reset, InvertedDepth, ColorArray, DepthArray, MotionArray;
        }

        private sealed class FallbackData
        {
            public TextureHandle Source, Destination;
            public Vector2Int InputSize, OutputSize;
        }

        private static readonly int ColorId = UnityShader.PropertyToID("_DlssInputColor");
        private static readonly int DepthId = UnityShader.PropertyToID("_DlssInputDepth");
        private static readonly int MotionId = UnityShader.PropertyToID("_DlssInputMotion");
        private static readonly int OutputId = UnityShader.PropertyToID("_DlssOutput");
        private static readonly int DestinationId = UnityShader.PropertyToID("_DlssDestination");
        private static readonly int OutputSizeId = UnityShader.PropertyToID("_DlssOutputSize");
        private static DlssUpscaler s_Live;
        private readonly Dictionary<ulong, DlssCameraContext> _contexts = new();
        private readonly List<ulong> _deadCameras = new();
        private readonly DlssUpscalerOptions _options;
        private readonly bool _ownsOptions;
        private Material _material;
        private ComputeShader _resolveShader;
        private int _resolve2D, _resolveArray;
        private Vector2Int _inputResolution = Vector2Int.one;
        private Vector2Int _outputResolution = Vector2Int.one;
        private bool _disposed, _warned;

        public static Vector2Int LastInputSize { get; private set; }
        public static Vector2Int LastOutputSize { get; private set; }
        public static int RecordedFrames { get; private set; }

        public DlssUpscaler(DlssUpscalerOptions options)
        {
            _ownsOptions = options == null;
            _options = options != null ? options : ScriptableObject.CreateInstance<DlssUpscalerOptions>();
            _options.upscalerName = UpscalerName;
            // HDRP runs AfterPostProcess volumes (including SDR NR) before this upscaler.
            _options.injectionPoint = DynamicResolutionHandler.UpsamplerScheduleType.AfterPost;
            s_Live?.Dispose();
            s_Live = this;
            RhiDomainReload.RegisterOwner(this);
        }

        public override string name => UpscalerName;
        public override UpscalerOptions options => _options;
        public override bool isTemporal => true;
        public override bool supportsSharpening => false;
        public override bool supportsXR => false;
        private UpscalerMode ActiveMode => DlssUpscalerOptions.Sanitize(_options.qualityMode);

        public override void CalculateJitter(int frameIndex, out Vector2 jitter, out bool allowScaling)
        {
            jitter = Vector2.zero;
            allowScaling = false;
            // The unsupported schedule and old-DLL fallback cannot resolve jitter.
            // Check global capability here, not another camera's last pass result.
            if (_options.injectionPoint != DynamicResolutionHandler.UpsamplerScheduleType.AfterPost)
                return;
            try
            {
                if (!RhiCore.IsD3D12Active || !RhiCore.IsNgxDlssAvailable || RhiCore.NativeApiVersion < 11)
                    return;
            }
            catch (DllNotFoundException) { return; }
            catch (EntryPointNotFoundException) { return; }

            float ratio = (float)Mathf.Max(1, _outputResolution.x) / Mathf.Max(1, _inputResolution.x);
            int phases = Mathf.Max(1, (int)(8f * ratio * ratio));
            int index = frameIndex % phases + 1;
            jitter = new Vector2(HaltonSequence.Get(index, 2) - 0.5f,
                HaltonSequence.Get(index, 3) - 0.5f);
            allowScaling = false;
        }

        public override void NegotiatePreUpscaleResolution(ref Vector2Int preUpscaleResolution,
            Vector2Int postUpscaleResolution)
        {
            _outputResolution = postUpscaleResolution;
            if (ActiveMode == UpscalerMode.NATIVE)
                preUpscaleResolution = postUpscaleResolution;
            else if (_options.fixedResolutionMode)
            {
                int width = 0, height = 0;
                bool optimal = false;
                try
                {
                    optimal = RhiCore.QueryDlssOptimalSettings(postUpscaleResolution.x,
                        postUpscaleResolution.y, ActiveMode, out width, out height);
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                if (!optimal)
                {
                    float scale = DlssUpscalerOptions.FallbackScale(ActiveMode);
                    width = Mathf.RoundToInt(postUpscaleResolution.x * scale);
                    height = Mathf.RoundToInt(postUpscaleResolution.y * scale);
                }
                preUpscaleResolution = new Vector2Int(Mathf.Max(1, width), Mathf.Max(1, height));
            }
            _inputResolution = preUpscaleResolution;
        }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            UpscalingIO io = frameData.Get<UpscalingIO>();
            Vector2Int input = io.preUpscaleResolution;
            Vector2Int outputSize = io.postUpscaleResolution;
            if (!io.cameraColor.IsValid() || input.x <= 0 || input.y <= 0 ||
                outputSize.x <= 0 || outputSize.y <= 0)
                return;

            Camera camera = FindCamera(io.cameraInstanceID);
            DlssCameraContext context;
            try
            {
                if (_disposed || camera == null || camera.cameraType != CameraType.Game ||
                    camera.stereoEnabled || io.numActiveViews > 1 ||
                    !io.cameraDepth.IsValid() || !io.motionVectorColor.IsValid() ||
                    !IsMonoTexture(graph, io.cameraColor) || !IsMonoTexture(graph, io.cameraDepth) ||
                    !IsMonoTexture(graph, io.motionVectorColor) ||
                    input.x > outputSize.x || input.y > outputSize.y ||
                    outputSize.x > ushort.MaxValue || outputSize.y > ushort.MaxValue ||
                    io.jitteredMotionVectors ||
                    _options.injectionPoint != DynamicResolutionHandler.UpsamplerScheduleType.AfterPost ||
                    !HDCamera.GetOrCreate(camera).frameSettings.IsEnabled(FrameSettingsField.Postprocess) ||
                    !CoreUtils.ArePostProcessesEnabled(camera) ||
                    (HDROutputSettings.main != null && HDROutputSettings.main.active) ||
                    !HDCamera.GetOrCreate(camera).frameSettings.IsEnabled(FrameSettingsField.MotionVectors) ||
                    !RhiCore.IsD3D12Active || !RhiCore.IsNgxDlssAvailable || !EnsureMaterial())
                {
                    WarnOnce("Unsupported camera, missing SDR post-process inputs, or unavailable NGX; using spatial upscaling.");
                    io.cameraColor = AddFallback(graph, io.cameraColor, input, outputSize);
                    return;
                }

                if (RhiCore.NativeApiVersion < 11)
                {
                    WarnOnce("NR-before-SR requires UnityRHI native API 11. Install the updated UnityRHI.dll and restart Unity; using spatial upscaling until then.");
                    io.cameraColor = AddFallback(graph, io.cameraColor, input, outputSize);
                    return;
                }

                PruneDeadCameras();
                context = GetOrCreateContext(io.cameraInstanceID, camera, input, outputSize);
            }
            catch (Exception exception)
            {
                WarnOnce($"Cannot prepare DLSS; using spatial upscaling. {exception.Message}");
                io.cameraColor = AddFallback(graph, io.cameraColor, input, outputSize);
                return;
            }

            TextureHandle color = graph.ImportTexture(context.ColorHandle);
            TextureHandle depth = graph.ImportTexture(context.DepthHandle);
            TextureHandle motion = graph.ImportTexture(context.MotionHandle);
            TextureHandle nativeOutput = graph.ImportTexture(context.OutputHandle);
            var sourceDescriptor = io.cameraColor.GetDescriptor(graph);
            TextureHandle destination = graph.CreateTexture(new RgTextureDesc(outputSize.x, outputSize.y)
            {
                format = GraphicsFormat.R16G16B16A16_SFloat,
                dimension = sourceDescriptor.dimension,
                slices = sourceDescriptor.slices,
                name = "DLSS HDRP Output",
                filterMode = FilterMode.Bilinear,
                enableRandomWrite = true,
                useDynamicScale = false,
                useDynamicScaleExplicit = false,
            });
            float sign = io.motionVectorDirection ==
                UpscalingIO.MotionVectorDirection.PreviousFrameToCurrentFrame ? -1f : 1f;
            Vector2 motionScale = io.motionVectorDomain == UpscalingIO.MotionVectorDomain.NDC
                ? (Vector2)io.motionVectorTextureSize : Vector2.one;

            using (var builder = graph.AddUnsafePass<PassData>("UnityRHI DLSS SR", out var data,
                new ProfilingSampler("UnityRHI DLSS SR")))
            {
                builder.UseTexture(io.cameraColor, AccessFlags.Read);
                builder.UseTexture(io.cameraDepth, AccessFlags.Read);
                builder.UseTexture(io.motionVectorColor, AccessFlags.Read);
                builder.UseTexture(color, AccessFlags.ReadWrite);
                builder.UseTexture(depth, AccessFlags.ReadWrite);
                builder.UseTexture(motion, AccessFlags.ReadWrite);
                builder.UseTexture(nativeOutput, AccessFlags.ReadWrite);
                builder.UseTexture(destination, AccessFlags.WriteAll);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                data.Color = io.cameraColor;
                data.Depth = io.cameraDepth;
                data.Motion = io.motionVectorColor;
                data.Destination = destination;
                data.Material = _material;
                data.ResolveShader = _resolveShader;
                data.ResolveKernel = sourceDescriptor.dimension == UnityEngine.Rendering.TextureDimension.Tex2DArray
                    ? _resolveArray : _resolve2D;
                data.Context = context;
                // HDRP negates CalculateJitter and may apply its camera jitter scale.
                // Read the actual camera value, even when another camera was updated later.
                data.Jitter = -(Vector2)HDCamera.GetOrCreate(camera).taaJitter;
                data.MotionScale = sign * motionScale;
                data.Reset = context.BeginFrame(io.frameIndex, io.resetHistory);
                data.InvertedDepth = io.invertedDepth;
                data.ColorArray = IsArray(graph, io.cameraColor);
                data.DepthArray = IsArray(graph, io.cameraDepth);
                data.MotionArray = IsArray(graph, io.motionVectorColor);
                builder.SetRenderFunc(static (PassData pass, UnsafeGraphContext graphContext) =>
                {
                    var command = CommandBufferHelpers.GetNativeCommandBuffer(graphContext.cmd);
                    var properties = graphContext.renderGraphPool.GetTempMaterialPropertyBlock();
                    var state = pass.Context;
                    properties.SetTexture(ColorId, pass.Color);
                    properties.SetTexture(DepthId, pass.Depth);
                    properties.SetTexture(MotionId, pass.Motion);
                    command.SetKeyword(pass.Material, new LocalKeyword(pass.Material.shader, "DLSS_COLOR_ARRAY"), pass.ColorArray);
                    command.SetKeyword(pass.Material, new LocalKeyword(pass.Material.shader, "DLSS_DEPTH_ARRAY"), pass.DepthArray);
                    command.SetKeyword(pass.Material, new LocalKeyword(pass.Material.shader, "DLSS_MOTION_ARRAY"), pass.MotionArray);
                    RenderTargetIdentifier[] targets = { state.ColorRt, state.MotionRt, state.DepthRt };
                    command.SetRenderTarget(targets, BuiltinRenderTextureType.None);
                    command.SetViewport(new Rect(0, 0, state.InputSize.x, state.InputSize.y));
                    command.DrawProcedural(Matrix4x4.identity, pass.Material, 0,
                        MeshTopology.Triangles, 3, 1, properties);

                    // Prefill at output size. The native backend can leave this image in
                    // place on a failed SR creation, with no invalid cross-size copy.
                    properties.SetTexture(ColorId, state.ColorRt);
                    command.SetRenderTarget(state.OutputRt);
                    command.SetViewport(new Rect(0, 0, state.OutputSize.x, state.OutputSize.y));
                    command.DrawProcedural(Matrix4x4.identity, pass.Material, 1,
                        MeshTopology.Triangles, 3, 1, properties);
                    try
                    {
                        state.Record(command, pass.Jitter, pass.MotionScale, pass.Reset, pass.InvertedDepth);
                        RecordedFrames++;
                    }
                    catch (Exception exception)
                    {
                        state.ResetHistory();
                        s_Live?.WarnOnce($"Native recording failed. {exception.Message}");
                    }

                    // The native backend clears D3D12 graphics state. Resolve through
                    // compute so the handoff does not depend on cached raster state.
                    // NGX does not produce alpha; restore it from the prepared color.
                    RTHandle destinationHandle = pass.Destination;
                    command.SetComputeTextureParam(pass.ResolveShader, pass.ResolveKernel, ColorId, state.ColorRt);
                    command.SetComputeTextureParam(pass.ResolveShader, pass.ResolveKernel, OutputId, state.OutputRt);
                    command.SetComputeTextureParam(pass.ResolveShader, pass.ResolveKernel, DestinationId, destinationHandle);
                    command.SetComputeIntParams(pass.ResolveShader, OutputSizeId, state.OutputSize.x, state.OutputSize.y);
                    command.DispatchCompute(pass.ResolveShader, pass.ResolveKernel,
                        (state.OutputSize.x + 7) / 8, (state.OutputSize.y + 7) / 8, 1);
                });
            }
            LastInputSize = input;
            LastOutputSize = outputSize;
            io.cameraColor = destination;
        }

        private static bool IsMonoTexture(RenderGraph graph, TextureHandle texture)
        {
            var desc = texture.GetDescriptor(graph);
            return (desc.dimension == UnityEngine.Rendering.TextureDimension.Tex2D ||
                (desc.dimension == UnityEngine.Rendering.TextureDimension.Tex2DArray && desc.slices == 1)) &&
                desc.msaaSamples == MSAASamples.None;
        }

        private static bool IsArray(RenderGraph graph, TextureHandle texture) =>
            texture.GetDescriptor(graph).dimension == UnityEngine.Rendering.TextureDimension.Tex2DArray;

        private Camera FindCamera(ulong id)
        {
            if (_contexts.TryGetValue(id, out var context) && context.Camera != null)
                return context.Camera;
            foreach (Camera camera in Camera.allCameras)
                if (EntityId.ToULong(camera.GetEntityId()) == id)
                    return camera;
            return null;
        }

        private DlssCameraContext GetOrCreateContext(ulong id, Camera camera, Vector2Int input, Vector2Int output)
        {
            if (_contexts.TryGetValue(id, out var context))
            {
                if (context.Matches(input, output, ActiveMode, _options.preset))
                    return context;
                context.Dispose();
                _contexts.Remove(id);
            }
            context = new DlssCameraContext(camera, input, output, ActiveMode, _options.preset);
            _contexts.Add(id, context);
            return context;
        }

        private void PruneDeadCameras()
        {
            _deadCameras.Clear();
            foreach (var pair in _contexts)
                if (pair.Value.Camera == null)
                    _deadCameras.Add(pair.Key);
            foreach (ulong id in _deadCameras)
            {
                _contexts[id].Dispose();
                _contexts.Remove(id);
            }
        }

        private bool EnsureMaterial()
        {
            if (_material == null)
            {
                var shader = Resources.Load<UnityShader>("UnityRhiDlssPrepareInputs");
                if (shader == null || !shader.isSupported) return false;
                _material = CoreUtils.CreateEngineMaterial(shader);
            }
            if (_resolveShader == null)
            {
                _resolveShader = Resources.Load<ComputeShader>("UnityRhiDlssResolve");
                if (_resolveShader == null) return false;
                _resolve2D = _resolveShader.FindKernel("Resolve2D");
                _resolveArray = _resolveShader.FindKernel("ResolveArray");
            }
            return true;
        }

        private static TextureHandle AddFallback(RenderGraph graph, TextureHandle input,
            Vector2Int inputSize, Vector2Int size)
        {
            var source = input.GetDescriptor(graph);
            var desc = new RgTextureDesc(size.x, size.y)
            {
                format = source.format,
                dimension = source.dimension,
                slices = source.slices,
                name = "DLSS Spatial Fallback",
                filterMode = FilterMode.Bilinear,
                useDynamicScale = false,
                useDynamicScaleExplicit = false,
            };
            TextureHandle output = graph.CreateTexture(desc);
            using (var builder = graph.AddUnsafePass<FallbackData>("DLSS Spatial Fallback", out var data))
            {
                builder.UseTexture(input, AccessFlags.Read);
                builder.UseTexture(output, AccessFlags.WriteAll);
                data.Source = input;
                data.Destination = output;
                data.InputSize = inputSize;
                data.OutputSize = size;
                builder.SetRenderFunc(static (FallbackData pass, UnsafeGraphContext context) =>
                {
                    RTHandle sourceHandle = pass.Source;
                    var allocation = new Vector2Int(sourceHandle.rt.width, sourceHandle.rt.height);
                    if (sourceHandle.rt.useDynamicScale &&
                        DynamicResolutionHandler.instance.HardwareDynamicResIsEnabled())
                        allocation = DynamicResolutionHandler.instance.ApplyScalesOnSize(allocation);
                    var scaleBias = new Vector4(
                        (float)pass.InputSize.x / Mathf.Max(1, allocation.x),
                        (float)pass.InputSize.y / Mathf.Max(1, allocation.y), 0, 0);
                    var command = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    context.cmd.SetRenderTarget(pass.Destination, 0, CubemapFace.Unknown, -1);
                    command.SetViewport(new Rect(0, 0, pass.OutputSize.x, pass.OutputSize.y));
                    Blitter.BlitTexture(command, sourceHandle, scaleBias, 0, true);
                });
            }
            return output;
        }

        private void WarnOnce(string message)
        {
            if (_warned) return;
            _warned = true;
            Debug.LogWarning($"[UnityRHI.DLSS] {message}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            foreach (var context in _contexts.Values) context.Dispose();
            _contexts.Clear();
            CoreUtils.Destroy(_material);
            _material = null;
            if (_ownsOptions) CoreUtils.Destroy(_options);
            _disposed = true;
            RhiDomainReload.UnregisterOwner(this);
            if (ReferenceEquals(s_Live, this)) s_Live = null;
        }

        public static void ReleaseResources() => s_Live?.Dispose();
    }

#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    internal static class DlssUpscalerRegistration
    {
        static DlssUpscalerRegistration() => Register();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeRuntime() => Register();

        private static void Register()
        {
            UpscalerRegistry.Register<DlssUpscaler, DlssUpscalerOptions>(DlssUpscaler.UpscalerName);
            RenderPipelineManager.activeRenderPipelineDisposed -= DlssUpscaler.ReleaseResources;
            RenderPipelineManager.activeRenderPipelineDisposed += DlssUpscaler.ReleaseResources;
            Application.quitting -= DlssUpscaler.ReleaseResources;
            Application.quitting += DlssUpscaler.ReleaseResources;
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= DlssUpscaler.ReleaseResources;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DlssUpscaler.ReleaseResources;
#endif
        }
    }
}
#endif
