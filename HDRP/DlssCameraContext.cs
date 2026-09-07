#if ENABLE_UPSCALER_FRAMEWORK
using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using RhiTexture = UnityRhi.Texture;

namespace UnityRhi.Dlss.Hdrp
{
    /// <summary>Fixed-size native inputs and temporal history for one mono HDRP camera.</summary>
    internal sealed class DlssCameraContext : IDisposable
    {
        internal Camera Camera { get; }
        internal Vector2Int InputSize { get; }
        internal Vector2Int OutputSize { get; }
        internal UpscalerMode Mode { get; }
        internal DlssPreset Preset { get; }
        internal RenderTexture ColorRt { get; private set; }
        internal RenderTexture MotionRt { get; private set; }
        internal RenderTexture DepthRt { get; private set; }
        internal RenderTexture OutputRt { get; private set; }
        internal RTHandle ColorHandle { get; private set; }
        internal RTHandle MotionHandle { get; private set; }
        internal RTHandle DepthHandle { get; private set; }
        internal RTHandle OutputHandle { get; private set; }

        private RhiTexture _color, _motion, _depth, _output;
        private DlssContext _dlss;
        private CommandList _commands;
        private bool _hasHistory, _disposed, _submitted;
        private int _lastFrame;
        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private Matrix4x4 _lastProjection;

        internal DlssCameraContext(Camera camera, Vector2Int input, Vector2Int output,
            UpscalerMode mode, DlssPreset preset)
        {
            Camera = camera;
            InputSize = input;
            OutputSize = output;
            Mode = mode;
            Preset = preset;
            try
            {
                ColorRt = Create("Color", input, GraphicsFormat.R16G16B16A16_SFloat, false);
                MotionRt = Create("Motion", input, GraphicsFormat.R16G16_SFloat, false);
                DepthRt = Create("Depth", input, GraphicsFormat.R32_SFloat, false);
                OutputRt = Create("Output", output, GraphicsFormat.R16G16B16A16_SFloat, true);
                ColorHandle = Allocate(ColorRt, input);
                MotionHandle = Allocate(MotionRt, input);
                DepthHandle = Allocate(DepthRt, input);
                OutputHandle = Allocate(OutputRt, output);
                _color = Wrap(ColorRt, Format.RGBA16_FLOAT);
                _motion = Wrap(MotionRt, Format.RG16_FLOAT);
                _depth = Wrap(DepthRt, Format.R32_FLOAT);
                // The fallback draw leaves Output in RenderTarget, not UAV state.
                _output = Wrap(OutputRt, Format.RGBA16_FLOAT);
                _dlss = new DlssContext(hdrInput: false);
                _commands = new CommandList(8);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal bool Matches(Vector2Int input, Vector2Int output, UpscalerMode mode, DlssPreset preset) =>
            !_disposed && input == InputSize && output == OutputSize && mode == Mode && preset == Preset;

        internal bool BeginFrame(int frameIndex, bool forceReset)
        {
            // HDRP's taaFrameIndex wraps at 1024 and advances in Edit Mode too.
            bool reset = forceReset || !_hasHistory || frameIndex != ((_lastFrame + 1) & 1023);
            Vector3 position = Camera.transform.position;
            Quaternion rotation = Camera.transform.rotation;
            Matrix4x4 projection = Camera.nonJitteredProjectionMatrix;
            if (_hasHistory)
            {
                reset |= Vector3.Distance(position, _lastPosition) > 5f ||
                    Quaternion.Angle(rotation, _lastRotation) > 45f;
                for (int i = 0; i < 16; i++)
                    reset |= Mathf.Abs(projection[i] - _lastProjection[i]) > 1e-4f;
            }
            _lastFrame = frameIndex;
            _lastPosition = position;
            _lastRotation = rotation;
            _lastProjection = projection;
            _hasHistory = true;
            return reset;
        }

        internal void ResetHistory() => _hasHistory = false;

        internal void Record(CommandBuffer command, Vector2 jitter, Vector2 motionScale,
            bool reset, bool invertedDepth)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DlssCameraContext));
            Device.Instance.RunGarbageCollection();
            _commands.Open();
            try
            {
                _commands.BeginMarker("HDRP.DLSS-SR");
                _dlss.Record(_commands, new DlssDispatchDesc
                {
                    Input = _color,
                    Output = _output,
                    MotionVectors = _motion,
                    Depth = _depth,
                    CameraJitterPixels = jitter,
                    RenderWidth = InputSize.x,
                    RenderHeight = InputSize.y,
                    OutputWidth = OutputSize.x,
                    OutputHeight = OutputSize.y,
                    MotionVectorScaleX = motionScale.x,
                    MotionVectorScaleY = motionScale.y,
                    Mode = Mode,
                    Preset = Preset,
                    Reset = reset,
                    DepthInverted = invertedDepth,
                });
                _commands.EndMarker();
                _commands.Close();
                _commands.SubmitAndForget(command);
                _submitted = true;
                // The native replay clears D3D12 graphics state that HDRP keeps
                // cached. Start a fresh command list before subsequent raster work.
                // This submits GPU work; it does not wait for GPU completion.
                RhiCore.SignalSyncPoint(command);
            }
            catch
            {
                ResetHistory();
                _commands.Dispose();
                _commands = new CommandList(8);
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            // NGX handles are not retained by command submissions. Wait before destroying
            // them, including when changing quality with frames in flight.
            if (_submitted && RhiCore.IsD3D12Active && !RhiCore.WaitForGpuIdle())
                throw new InvalidOperationException("DLSS resources are still in use by the GPU.");
            _disposed = true;
            _commands?.Dispose();
            _dlss?.Dispose();
            _output?.Dispose();
            _depth?.Dispose();
            _motion?.Dispose();
            _color?.Dispose();
            OutputHandle?.Release();
            DepthHandle?.Release();
            MotionHandle?.Release();
            ColorHandle?.Release();
            Destroy(OutputRt);
            Destroy(DepthRt);
            Destroy(MotionRt);
            Destroy(ColorRt);
        }

        private RenderTexture Create(string suffix, Vector2Int size, GraphicsFormat format, bool uav)
        {
            var texture = new RenderTexture(new RenderTextureDescriptor(size.x, size.y)
            {
                graphicsFormat = format,
                depthStencilFormat = GraphicsFormat.None,
                dimension = UnityEngine.Rendering.TextureDimension.Tex2D,
                msaaSamples = 1,
                volumeDepth = 1,
                enableRandomWrite = uav,
                useDynamicScale = false,
                sRGB = false,
                useMipMap = false,
            })
            {
                name = $"DLSS {Camera.name} {suffix}",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!texture.Create())
            {
                Destroy(texture);
                throw new InvalidOperationException($"Cannot allocate DLSS {suffix} at {size}.");
            }
            return texture;
        }

        private static RTHandle Allocate(RenderTexture texture, Vector2Int size)
        {
            RTHandle handle = RTHandles.Alloc(texture);
            var properties = handle.rtHandleProperties;
            properties.rtHandleScale = Vector4.one;
            properties.currentViewportSize = properties.previousViewportSize = size;
            properties.currentRenderTargetSize = properties.previousRenderTargetSize = size;
            handle.SetCustomHandleProperties(properties);
            return handle;
        }

        private static RhiTexture Wrap(RenderTexture texture, Format format) =>
            Device.Instance.CreateTextureFromNativeResource(texture.GetNativeTexturePtr(), new TextureDesc
            {
                Width = (uint)texture.width,
                Height = (uint)texture.height,
                Format = format,
                IsShaderResource = true,
                IsRenderTarget = true,
                IsUAV = texture.enableRandomWrite,
                InitialState = ResourceStates.RenderTarget,
                KeepInitialState = true,
                DebugName = texture.name,
            });

        private static void Destroy(RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
#endif
