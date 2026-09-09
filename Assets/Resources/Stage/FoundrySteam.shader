Shader "Saber/Foundry Steam"
{
    Properties { _MotionTime ("Motion clock", Float) = 0 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent-10" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float _MotionTime;
        CBUFFER_END
        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
            half4 color : COLOR;
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            float3 positionWS : TEXCOORD1;
            half4 color : COLOR;
            float4 screen : TEXCOORD2;
            half fog : TEXCOORD3;
        };
        Varyings Vert(Attributes input)
        {
            Varyings output;
            VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
            output.positionCS = pos.positionCS;
            output.positionWS = pos.positionWS;
            output.screen = ComputeScreenPos(pos.positionCS);
            output.uv = input.uv;
            output.color = input.color;
            output.fog = ComputeFogFactor(pos.positionCS.z);
            return output;
        }
        float Hash(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }
        float Noise(float2 p)
        {
            float2 cell = floor(p), f = frac(p);
            f = f * f * (3 - 2 * f);
            return lerp(lerp(Hash(cell), Hash(cell + float2(1, 0)), f.x),
                        lerp(Hash(cell + float2(0, 1)), Hash(cell + 1), f.x), f.y);
        }
        half4 Frag(Varyings input) : SV_Target
        {
            float2 p = input.uv * 2 - 1;
            float clock = _MotionTime * (6.2831853 / 120.0);
            float2 drift = float2(sin(clock), cos(clock)) * 2.2;
            float n = Noise(p * 3.1 + drift) * .7
                    + Noise(p * 6.5 - drift.yx * .8) * .3;
            float edge = saturate((1 - dot(p, p)) * 1.8);
            edge = edge * edge * (3 - 2 * edge);
            // 深度テクスチャ不要。通路側と画面中央は段階的に透過し、ノーツを覆わない。
            float corridor = smoothstep(5.8, 6.35, abs(input.positionWS.x));
            float screenX = input.screen.x / input.screen.w;
            float clearCenter = smoothstep(.18, .27, abs(screenX - .5));
            half alpha = input.color.a * edge * smoothstep(.16, .79, n) * corridor * clearCenter;
            half3 color = MixFog(input.color.rgb * (.80 + .24 * n), input.fog);
            return half4(color, alpha);
        }
        ENDHLSL
        Pass
        {
            Name "Steam2DRenderer"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            ENDHLSL
        }
        Pass
        {
            Name "SteamForwardRenderer"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            ENDHLSL
        }
    }
}
