Shader "Hidden/UnityRHI/DLSS-NR/PrepareInputs"
{
    SubShader
    {
        // Shared Core/TextureXR includes provide the fullscreen and sampling helpers used by HDRP.
        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            Name "PrepareInputs"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _USE_DRAW_PROCEDURAL

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/TextureXR.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"

            TEXTURE2D_X(_DlssNrInputColor);
            // HDRP binds these resources globally immediately before invoking the
            // custom post process.
            TEXTURE2D_X(_CameraDepthTexture);
            TEXTURE2D_X(_CameraMotionVectorsTexture);
            float4 _DlssNrInputScale;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            struct Outputs
            {
                float4 color : SV_Target0;
                float2 motion : SV_Target1;
                float depth : SV_Target2;
                float4 fallback : SV_Target3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            Outputs Frag(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                Outputs output;
                float2 colorUv = input.uv * _DlssNrInputScale.xy;
                output.color = SAMPLE_TEXTURE2D_X(_DlssNrInputColor,
                    sampler_LinearClamp, colorUv);
                output.fallback = output.color;

                // Depth and motion remain at the camera render resolution even
                // when the post-process color uses a different RTHandle scale.
                // HDRP reads these buffers in integer screen pixels; reusing the
                // color UV scale can sample padding or an unrelated sub-rectangle.
                uint2 pixelCoord = uint2(input.positionCS.xy);
                output.depth = LOAD_TEXTURE2D_X_LOD(_CameraDepthTexture,
                    pixelCoord, 0).r;

                // HDRP encodes pixels without a valid motion vector using x > 1.
                // Decode that sentinel before copying into the RG16F native input;
                // otherwise it becomes a saturated full-screen motion vector.
                float4 encodedMotion = LOAD_TEXTURE2D_X_LOD(
                    _CameraMotionVectorsTexture, pixelCoord, 0);
                output.motion = encodedMotion.x > 1.0f
                    ? float2(0.0f, 0.0f)
                    : encodedMotion.xy;
                return output;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DebugInputs"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex VertDebug
            #pragma fragment FragDebug
            #pragma multi_compile _ _USE_DRAW_PROCEDURAL

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/TextureXR.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"

            // These are the fixed mono RenderTextures allocated by
            // DlssNrCameraContext. The Game path deliberately rejects stereo,
            // so declaring them as TEXTURE2D_X can incorrectly expand them to
            // Texture2DArray and make Unity bind UnityDefault2DArray instead.
            TEXTURE2D(_DlssNrInputDepth);
            TEXTURE2D(_DlssNrInputMotion);

            int _DlssNrDebugMode;
            float _DlssNrDebugMotionScaleX;
            float _DlssNrDebugMotionScaleY;
            float _DlssNrDebugMotionRange;
            float _DlssNrDebugDepthRange;
            float4 _ZBufferParams;

            struct DebugAttributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DebugVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DebugVaryings VertDebug(DebugAttributes input)
            {
                DebugVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            float3 MotionHeatmap(float value)
            {
                value = saturate(value);
                return saturate(float3(
                    1.5 - abs(4.0 * value - 3.0),
                    1.5 - abs(4.0 * value - 2.0),
                    1.5 - abs(4.0 * value - 1.0)));
            }

            float4 FragDebug(DebugVaryings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                if (_DlssNrDebugMode == 1 || _DlssNrDebugMode == 2)
                {
                    // The debug target and prepared inputs have identical fixed
                    // dimensions. Load by integer pixel so the visualization is
                    // exactly the data handed to native, without another UV or
                    // RTHandle scale conversion.
                    uint2 pixelCoord = uint2(input.positionCS.xy);
                    float2 rawMotion = LOAD_TEXTURE2D_LOD(
                        _DlssNrInputMotion, pixelCoord, 0).xy;
                    float2 motionPixels = rawMotion * float2(
                        _DlssNrDebugMotionScaleX, _DlssNrDebugMotionScaleY);
                    float range = max(_DlssNrDebugMotionRange, 1e-4);
                    float magnitude = length(motionPixels) / range;

                    if (_DlssNrDebugMode == 1)
                    {
                        // Zero motion is neutral gray. Red/green encode signed
                        // current-to-previous X/Y motion; blue encodes magnitude.
                        return float4(
                            saturate(0.5 + motionPixels.x / (2.0 * range)),
                            saturate(0.5 + motionPixels.y / (2.0 * range)),
                            saturate(magnitude), 1.0);
                    }

                    return float4(MotionHeatmap(magnitude), 1.0);
                }

                uint2 pixelCoord = uint2(input.positionCS.xy);
                float rawDepth = LOAD_TEXTURE2D_LOD(
                    _DlssNrInputDepth, pixelCoord, 0).r;
                if (_DlssNrDebugMode == 3)
                    return float4(rawDepth.xxx, 1.0);

                float eyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float normalizedDepth = saturate(eyeDepth / max(_DlssNrDebugDepthRange, 1e-4));
                return float4(normalizedDepth.xxx, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
