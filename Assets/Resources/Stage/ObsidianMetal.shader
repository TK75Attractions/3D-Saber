Shader "Saber/Obsidian Stage Metal"
{
    Properties
    {
        _BaseColor ("Surface", Color) = (0.06, 0.08, 0.10, 1)
        _EmissionColor ("Inlay", Color) = (0, 0, 0, 1)
        _Smoothness ("Satin finish", Range(0,1)) = 0.35
        _Metallic ("Metal", Range(0,1)) = 0.32
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Back
        ZWrite On
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            half4 _EmissionColor;
            half _Smoothness;
            half _Metallic;
        CBUFFER_END
        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            half fog : TEXCOORD2;
        };
        Varyings Vert(Attributes input)
        {
            Varyings output;
            VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
            output.positionCS = position.positionCS;
            output.positionWS = position.positionWS;
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.fog = ComputeFogFactor(position.positionCS.z);
            return output;
        }
        half4 Frag(Varyings input) : SV_Target
        {
            // 2D Renderer でも背景の面取りに陰影が付く、固定の環境照明。
            // プロジェクト全体のライト・ノーツ用シェーダーを変更しない。
            half3 normal = normalize(input.normalWS);
            half3 view = SafeNormalize(GetCameraPositionWS() - input.positionWS);
            half3 key = normalize(half3(-0.38, 0.80, -0.45));
            half3 fill = normalize(half3(0.70, 0.30, 0.20));
            half lighting = 0.70h + 0.72h * saturate(dot(normal, key)) + 0.18h * saturate(dot(normal, fill));
            half rim = pow(1.0h - saturate(dot(normal, view)), 4.0h) * _Smoothness;
            half highlight = pow(saturate(dot(reflect(-key, normal), view)), 36.0h) * _Smoothness;
            half3 color = _BaseColor.rgb * lighting + _EmissionColor.rgb * 0.35h;
            color += half3(0.032, 0.048, 0.062) * (rim + highlight * 0.45h);
            color = MixFog(color, input.fog);
            return half4(color, 1);
        }
        ENDHLSL
        Pass
        {
            Name "Stage2DRenderer"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            ENDHLSL
        }
        Pass
        {
            Name "StageForwardRenderer"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            ENDHLSL
        }
    }
}
