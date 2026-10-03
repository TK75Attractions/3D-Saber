Shader "SongSelect/GuideStudio"
{
    Properties
    {
        _BaseColor("Color", Color) = (0.78,0.88,0.94,1)
        _Emission("Emission", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            ZWrite On
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Emission;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; half3 normalWS : TEXCOORD0; float3 positionWS : TEXCOORD1; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                half3 n = normalize(input.normalWS);
                half3 v = normalize(GetWorldSpaceViewDir(input.positionWS));
                half3 key = normalize(half3(0.65,0.8,-0.65));
                half diffuse = 0.24 + 0.65 * saturate(dot(n,key));
                half fill = 0.16 * saturate(dot(n,normalize(half3(-0.8,0.2,-0.4))));
                half rim = pow(1-saturate(dot(n,v)),3) * 0.25;
                half spec = pow(saturate(dot(n,normalize(key+v))),32) * 0.22;
                half3 lit = _BaseColor.rgb * (diffuse+fill) + half3(.32,.60,.73)*rim + spec;
                return half4(lerp(lit,_BaseColor.rgb,saturate(_Emission)),1);
            }
            ENDHLSL
        }
    }
}
