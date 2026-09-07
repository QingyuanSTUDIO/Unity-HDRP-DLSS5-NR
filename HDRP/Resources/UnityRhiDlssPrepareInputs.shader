Shader "Hidden/UnityRHI/DLSS/HDRP/PrepareInputs"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        ZTest Always
        ZWrite Off
        Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        Varyings Vert(uint vertexID : SV_VertexID)
        {
            Varyings output;
            output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
            output.uv = GetFullScreenTriangleTexCoord(vertexID);
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "Prepare"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ DLSS_COLOR_ARRAY
            #pragma multi_compile_local _ DLSS_DEPTH_ARRAY
            #pragma multi_compile_local _ DLSS_MOTION_ARRAY
            #if defined(DLSS_COLOR_ARRAY)
                TEXTURE2D_ARRAY(_DlssInputColor);
            #else
                TEXTURE2D(_DlssInputColor);
            #endif
            #if defined(DLSS_DEPTH_ARRAY)
                TEXTURE2D_ARRAY(_DlssInputDepth);
            #else
                TEXTURE2D(_DlssInputDepth);
            #endif
            #if defined(DLSS_MOTION_ARRAY)
                TEXTURE2D_ARRAY(_DlssInputMotion);
            #else
                TEXTURE2D(_DlssInputMotion);
            #endif
            struct Outputs
            {
                float4 color : SV_Target0;
                float2 motion : SV_Target1;
                float depth : SV_Target2;
            };
            Outputs Frag(Varyings input)
            {
                Outputs output;
                // Copy the active HDRP viewport, never RTHandle allocation padding.
                uint2 pixel = uint2(input.positionCS.xy);
                #if defined(DLSS_COLOR_ARRAY)
                    output.color = LOAD_TEXTURE2D_ARRAY_LOD(_DlssInputColor, pixel, 0, 0);
                #else
                    output.color = LOAD_TEXTURE2D_LOD(_DlssInputColor, pixel, 0);
                #endif
                #if defined(DLSS_DEPTH_ARRAY)
                    output.depth = LOAD_TEXTURE2D_ARRAY_LOD(_DlssInputDepth, pixel, 0, 0).r;
                #else
                    output.depth = LOAD_TEXTURE2D_LOD(_DlssInputDepth, pixel, 0).r;
                #endif
                #if defined(DLSS_MOTION_ARRAY)
                    float2 motion = LOAD_TEXTURE2D_ARRAY_LOD(_DlssInputMotion, pixel, 0, 0).xy;
                #else
                    float2 motion = LOAD_TEXTURE2D_LOD(_DlssInputMotion, pixel, 0).xy;
                #endif
                output.motion = motion.x > 1.0 ? float2(0, 0) : motion;
                return output;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Fallback"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            TEXTURE2D(_DlssInputColor);
            float4 Frag(Varyings input) : SV_Target
            {
                return SAMPLE_TEXTURE2D(_DlssInputColor, sampler_LinearClamp, input.uv);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Resolve"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            TEXTURE2D(_DlssInputColor);
            TEXTURE2D(_DlssOutput);
            float4 Frag(Varyings input) : SV_Target
            {
                float3 color = LOAD_TEXTURE2D_LOD(_DlssOutput, uint2(input.positionCS.xy), 0).rgb;
                float alpha = SAMPLE_TEXTURE2D(_DlssInputColor, sampler_LinearClamp, input.uv).a;
                return float4(color, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
