Shader "Saber/Dark Veil"
{
    // 2:23 AM の Y(闇と灯り)の暗幕。画面を黒く覆い、判定面で剣の線分に近いところだけ穴を開ける。
    // 穴の式は DarkRules.VeilAlpha と同じ(FullRadius まで透明、FadeRadius で _Darkness)。
    Properties
    {
        _Darkness ("Darkness", Range(0, 1)) = 0
        _RedSegment ("Red blade (A.xy, B.xy)", Vector) = (0, 0, 0, 0)
        _BlueSegment ("Blue blade (A.xy, B.xy)", Vector) = (0, 0, 0, 0)
        _BladeMask ("Blade available (red, blue)", Vector) = (0, 0, 0, 0)
        _Radii ("Full / fade radius", Vector) = (.7, 1.3, 0, 0)
        _JudgeZ ("Judge plane z", Float) = 0
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
            float4 _RedSegment;
            float4 _BlueSegment;
            float4 _BladeMask;
            float4 _Radii;
            float _Darkness;
            float _JudgeZ;
        CBUFFER_END

        struct Attributes { float4 positionOS : POSITION; };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
        };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            return output;
        }

        float SegmentDistance(float2 p, float2 a, float2 b)
        {
            float2 d = b - a;
            float len2 = dot(d, d);
            float t = len2 > 1e-6 ? saturate(dot(p - a, d) / len2) : 0.0;
            return distance(p, a + d * t);
        }

        float Away(float distance)
        {
            float u = saturate((distance - _Radii.x) / max(1e-4, _Radii.y - _Radii.x));
            return u * u * (3.0 - 2.0 * u);
        }

        half4 Frag(Varyings input) : SV_Target
        {
            // この画素を通る視線が判定面(z = _JudgeZ)と交わる点で、剣の線分までの距離を測る。
            float3 eye = _WorldSpaceCameraPos;
            float3 ray = input.positionWS - eye;
            float away = 1.0;
            if (ray.z > 1e-5)
            {
                float2 p = eye.xy + ray.xy * ((_JudgeZ - eye.z) / ray.z);
                if (_BladeMask.x > .5) away = min(away, Away(SegmentDistance(p, _RedSegment.xy, _RedSegment.zw)));
                if (_BladeMask.y > .5) away = min(away, Away(SegmentDistance(p, _BlueSegment.xy, _BlueSegment.zw)));
            }
            return half4(0, 0, 0, saturate(_Darkness * away));
        }
        ENDHLSL

        Pass
        {
            Name "DarkVeil2DRenderer"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "DarkVeilForwardRenderer"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
