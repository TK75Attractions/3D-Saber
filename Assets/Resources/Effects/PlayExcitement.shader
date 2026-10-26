Shader "Saber/Play Excitement"
{
    Properties
    {
        _GateRect ("Protected gate", Vector) = (0,0,0,0)
        _ClipGate ("Clip gate", Float) = 0
        _ZTest ("Depth test", Float) = 4
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+11" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest [_ZTest]
        Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _GateRect;
            float _ClipGate;
        CBUFFER_END
        struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float4 screen : TEXCOORD0;
            float2 uv : TEXCOORD1;
            half4 color : COLOR;
        };
        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.screen = ComputeScreenPos(output.positionCS);
            output.uv = input.uv;
            output.color = input.color;
            return output;
        }
        half4 Frag(Varyings input) : SV_Target
        {
            float2 p = input.screen.xy / input.screen.w;
            // 輪・落下片・レーザーも、投影後のゲート内側へ入れない。
            if (_ClipGate > .5 && p.x >= _GateRect.x && p.x <= _GateRect.z
                && p.y >= _GateRect.y && p.y <= _GateRect.w) discard;
            half edge = saturate(1 - abs(input.uv.y * 2 - 1));
            return half4(input.color.rgb, input.color.a * edge);
        }
        ENDHLSL
        Pass
        {
            Name "Excitement2D"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
        Pass
        {
            Name "ExcitementForward"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
