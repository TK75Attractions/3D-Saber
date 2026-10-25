Shader "Saber/Memory Veil Noise"
{
    Properties
    {
        _Strength ("Strength", Range(0, 1)) = 0
        _Amber ("Amber", Color) = (1, .63, .24, 1)
        _NoiseFrame ("Noise Frame", Float) = 0
        _NoiseSeconds ("Noise Seconds", Float) = 0
        _Reduced ("Reduced Effects", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+40" "RenderType"="Transparent" }
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _Amber;
            float _Strength;
            float _NoiseFrame;
            float _NoiseSeconds;
            float _Reduced;
        CBUFFER_END

        struct Attributes { float4 positionOS : POSITION; };
        struct Varyings { float4 positionCS : SV_POSITION; };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            return output;
        }

        float Hash(float2 cell, float frame)
        {
            float3 p = frac(float3(cell.x, cell.y, frame) * float3(.1031, .11369, .13787));
            p += dot(p, p.yzx + 19.19);
            return frac((p.x + p.y) * p.z);
        }

        half4 Frag(Varyings input) : SV_Target
        {
            float2 pixel = input.positionCS.xy;
            // 粒は画面の高さの 1/270(1080p で 4 画素)。投影を離れて見ても灰色一色に溶けず、砂嵐に見える大きさ。
            float cellSize = max(2.0, floor(_ScreenParams.y / 270.0));
            float2 cell = floor(pixel / cellSize);
            float2 pair = float2(floor(cell.x * .5), cell.y);

            // 隣り合う2粒は同じ量だけ明暗を逆にする。
            // 入れ替わっても、その対の平均輝度は一定。画面全体の明滅を作らない。
            float polarity = fmod(cell.x, 2.0) < 1.0 ? -1.0 : 1.0;
            float grain = (Hash(pair, _NoiseFrame) * 2.0 - 1.0) * polarity;

            float y = pixel.y / max(1.0, _ScreenParams.y);
            float wave = .5 + .5 * cos(6.2831853 * (y - _NoiseSeconds * .045));
            float band = pow(wave, 16.0) * (1.0 - saturate(_Reduced));
            // 横帯も平均を持ち上げず、明暗の幅だけを広げる。
            float value = .15 + grain * (.10 + .05 * band);
            float scanline = 1.0 - step(1.0, fmod(floor(pixel.y / cellSize), 2.0));
            value -= scanline * .018;

            float3 tint = lerp(float3(1, 1, 1), _Amber.rgb, .09);
            return half4(value * tint, saturate(_Strength));
        }
        ENDHLSL

        Pass
        {
            Name "MemoryVeil2DRenderer"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "MemoryVeilForwardRenderer"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
