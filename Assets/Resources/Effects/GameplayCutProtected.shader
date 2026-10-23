Shader "Saber/Gameplay Cut Protected"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+10" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        StructuredBuffer<float4> _NoteMaskRects;
        int _NoteMaskCount;
        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 uv : TEXCOORD0;
            float2 protection : TEXCOORD1;
            half4 color : COLOR;
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 uv : TEXCOORD0;
            float4 screenPos : TEXCOORD1;
            float protection : TEXCOORD2;
            half4 color : COLOR;
        };
        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.screenPos = ComputeScreenPos(output.positionCS);
            output.uv = input.uv;
            output.protection = input.protection.x;
            output.color = input.color;
            return output;
        }
        half4 Frag(Varyings input) : SV_Target
        {
            float tip = smoothstep(0, .12, input.uv.x) * smoothstep(0, .12, 1 - input.uv.x);
            float edge = saturate(1 - abs(input.uv.y * 2 - 1));
            edge = lerp(edge, 1, saturate(input.uv.z));
            float keep = 1;
            if (input.protection > .5)
            {
                float2 p = input.screenPos.xy / input.screenPos.w;
                [loop] for (int i = 0; i < _NoteMaskCount; i++)
                {
                    float4 r = _NoteMaskRects[i];
                    if (all(p >= r.xy) && all(p <= r.zw)) { keep = .25; break; }
                }
            }
            return half4(input.color.rgb, input.color.a * tip * edge * keep);
        }
        ENDHLSL
        Pass
        {
            Name "CutProtected2D"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
        Pass
        {
            Name "CutProtectedForward"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
