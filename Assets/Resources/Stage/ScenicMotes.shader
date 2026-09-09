Shader "Saber/Scenic World Motes"
{
    Properties { _Bubble ("Bubble", Float) = 0 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent-20" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float _Bubble;
        CBUFFER_END
        struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; };
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 world : TEXCOORD1; float4 screen : TEXCOORD2; half4 color : COLOR; };
        Varyings Vert(Attributes input)
        {
            Varyings output;
            VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
            output.positionCS = pos.positionCS; output.world = pos.positionWS;
            output.screen = ComputeScreenPos(pos.positionCS); output.uv = input.uv; output.color = input.color;
            return output;
        }
        half4 Frag(Varyings input) : SV_Target
        {
            float2 p = input.uv * 2 - 1;
            float radius = length(p);
            float alpha = pow(saturate(1 - radius * radius),2);
            if (_Bubble > .5)
                alpha = exp(-pow((radius - .73) * 16,2)) * .65 + exp(-dot(p - float2(-.28,.32),p - float2(-.28,.32)) * 60) * .5;
            float x = input.screen.x / input.screen.w;
            alpha *= smoothstep(.16,.26,abs(x - .5)) * smoothstep(5.8,6.4,abs(input.world.x));
            return half4(input.color.rgb, input.color.a * alpha);
        }
        ENDHLSL
        Pass
        {
            Name "Motes2DRenderer"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
        Pass
        {
            Name "MotesForwardRenderer"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
