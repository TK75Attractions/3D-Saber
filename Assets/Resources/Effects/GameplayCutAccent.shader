Shader "Saber/Gameplay Cut Accent"
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
        struct Attributes { float4 positionOS : POSITION; float3 uv : TEXCOORD0; half4 color : COLOR; };
        struct Varyings { float4 positionCS : SV_POSITION; float3 uv : TEXCOORD0; half4 color : COLOR; };
        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.uv = input.uv;
            output.color = input.color;
            return output;
        }
        half4 Frag(Varyings input) : SV_Target
        {
            // 線の両端と側面だけを柔らかくし、矩形の板や大きな白い面を作らない。
            float tip = smoothstep(0, .12, input.uv.x) * smoothstep(0, .12, 1 - input.uv.x);
            float edge = saturate(1 - abs(input.uv.y * 2 - 1));
            // 刃の軌跡(uv.z=1)は隣の時刻の面へ連続させ、縞模様にしない。
            edge = lerp(edge, 1, saturate(input.uv.z));
            return half4(input.color.rgb, input.color.a * tip * edge);
        }
        ENDHLSL
        Pass
        {
            Name "CutAccent2DRenderer"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
        Pass
        {
            Name "CutAccentForwardRenderer"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
