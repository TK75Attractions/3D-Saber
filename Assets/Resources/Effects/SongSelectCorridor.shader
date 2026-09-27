Shader "Saber/Song Select Corridor"
{
    Properties
    {
        _Tint("Tint", Color) = (1,1,1,1)
        _Glow("Glow", Float) = 0
        _Fog("Fog", Float) = 1
        _Floor("Floor", Float) = 0
        _Drift("Dust drift", Float) = 0
        _SrcBlend("Source", Float) = 5
        _DstBlend("Destination", Float) = 10
        _ZWrite("Depth", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; };
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; float depth : TEXCOORD1; };
        CBUFFER_START(UnityPerMaterial)
        half4 _Tint; float _Glow, _Fog, _Floor, _Drift;
        CBUFFER_END
        Varyings Vert(Attributes i)
        {
            Varyings o;
            if (_Drift > .5)
            {
                float corner = (i.uv.y - .5) * .32;
                float center = i.positionOS.y - corner;
                i.positionOS.y = fmod(center + _Time.y * .35 + 2, 26) - 2 + corner;
            }
            float3 world = TransformObjectToWorld(i.positionOS.xyz);
            o.positionCS = TransformWorldToHClip(world); o.uv = i.uv; o.color = i.color;
            o.depth = abs(TransformWorldToView(world).z); return o;
        }
        half4 Frag(Varyings i) : SV_Target
        {
            half4 c = i.color * _Tint;
            if (_Glow > 1.5) c.a *= pow(saturate(1 - length(i.uv * 2 - 1)), 2.5);
            else if (_Glow > .5) c.a *= pow(saturate(1 - abs(i.uv.x * 2 - 1)), 2) * smoothstep(0, .03, i.uv.y) * smoothstep(0, .03, 1-i.uv.y);
            if (_Floor > .5) c.rgb = pow(lerp(half3(.008,.016,.04), half3(.047,.102,.20), pow(saturate(i.depth / 390), 2.2)), 2.2);
            float fog = saturate((i.depth - 30) / 185) * _Fog;
            c.rgb = lerp(c.rgb, pow(half3(.0196,.0353,.0784), 2.2), fog);
            if (_Glow > .5) c.a *= 1-fog;
            return c;
        }
        ENDHLSL
        Pass
        {
            Name "Corridor2D"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
        Pass
        {
            Name "CorridorForward"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
