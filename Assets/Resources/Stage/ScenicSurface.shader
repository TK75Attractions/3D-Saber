Shader "Saber/Scenic World Surface"
{
    Properties
    {
        _BaseColor ("Surface", Color) = (.2,.25,.3,1)
        _HazeColor ("Distance", Color) = (.03,.06,.09,1)
        _AccentColor ("Detail", Color) = (.3,.4,.5,1)
        _Emission ("Emission", Range(0,1)) = 0
        _Mode ("Stone 0 Water 1 Crystal 2 Sky 3 Planet 4", Float) = 0
        _Caustics ("Underwater light", Float) = 0
        _Sway ("Plant 1 Cloth -1", Float) = 0
        _AnchorY ("Bend anchor", Float) = -2.5
        _MotionTime ("Song clock", Float) = 0
        _Chorus ("Musical section", Range(0,1)) = 0
        _ChorusColor ("Section atmosphere", Color) = (.3,.3,.4,1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" "RenderType"="Opaque" }
        Cull Back
        ZWrite On
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor, _HazeColor, _AccentColor;
            float _Emission, _Mode, _Caustics, _Sway, _AnchorY, _MotionTime;
            float _Chorus;
            half4 _ChorusColor;
        CBUFFER_END
        struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
        struct Varyings { float4 positionCS : SV_POSITION; float3 world : TEXCOORD0; half3 normal : TEXCOORD1; float4 screen : TEXCOORD2; };
        Varyings Vert(Attributes input)
        {
            Varyings output;
            float3 p = input.positionOS.xyz;
            float height = max(0, _Sway > 0 ? p.y - _AnchorY : _AnchorY - p.y);
            p.x += abs(_Sway) * min(height * height, 36) * .012 * sin(_MotionTime * .85 + p.z * .46 + p.x * .24);
            p.z += abs(_Sway) * height * .023 * sin(_MotionTime * 1.24 + p.x * .63 + height * .75);
            if (_Mode > .5 && _Mode < 1.5) p.y += .018 * sin(p.x * 1.3 + _MotionTime) * cos(p.z * .8 - _MotionTime * .7);
            VertexPositionInputs position = GetVertexPositionInputs(p);
            output.positionCS = position.positionCS;
            output.world = position.positionWS;
            output.normal = TransformObjectToWorldNormal(input.normalOS);
            output.screen = ComputeScreenPos(position.positionCS);
            return output;
        }
        float Grain(float3 p) { return frac(sin(dot(floor(p * 27), float3(12.9898, 78.233, 45.164))) * 43758.5453); }
        half4 Frag(Varyings input) : SV_Target
        {
            float3 p = input.world;
            float2 screen = input.screen.xy / input.screen.w;
            half3 normal = normalize(input.normal);
            half3 view = SafeNormalize(GetCameraPositionWS() - p);
            half light = .67 + .51 * saturate(dot(normal, normalize(half3(-.45,.82,-.38)))) + .12 * saturate(normal.x);
            half3 color = _BaseColor.rgb * light * (.94 + .09 * Grain(p));
            color += _AccentColor.rgb * _Emission;
            if (_Mode > .5 && _Mode < 1.5)
            {
                float waves = sin(p.x * 2.7 + p.z * 1.9 + _MotionTime * 1.2) + sin(p.x * 1.3 - p.z * 2.1 - _MotionTime * .9);
                float rings = pow(saturate(sin(length(p.xz - float2(8,11)) * 10 - _MotionTime * 2.8)), 24);
                color = _BaseColor.rgb * (.82 + waves * .09) + _AccentColor.rgb * (.10 + pow(saturate(waves * .5), 10) * .20 + rings * .13);
            }
            if (_Mode > 1.5 && _Mode < 2.5)
            {
                half rim = pow(1 - saturate(abs(dot(normal, view))), 3);
                half shine = pow(saturate(sin(p.y * 1.4 - _MotionTime * .65 + p.z * .32)), 12);
                color += _AccentColor.rgb * (rim * .32 + shine * .08);
            }
            if (_Mode > 2.5 && _Mode < 3.5)
            {
                float horizon = smoothstep(-.12,.55, normalize(p - GetCameraPositionWS()).y);
                color = lerp(_HazeColor.rgb, _BaseColor.rgb, horizon);
                float cloud = sin(p.x * .035 + _MotionTime * .016) * sin(p.y * .07 + p.x * .014) * .5 + .5;
                color += _AccentColor.rgb * pow(cloud, 6) * .08;
                color += _ChorusColor.rgb * _Chorus * (.12 + .10 * pow(cloud,3));
                // スコアと曲名の背後は静かな濃色にして白文字の視認性を保つ。
                color *= 1 - smoothstep(.72,.96, screen.y) * .68;
                return half4(color,1);
            }
            if (_Mode > 3.5)
            {
                float bands = sin(p.y * .57 + sin(p.x * .18) * 1.2 + _MotionTime * .03) * .5 + .5;
                color = lerp(_BaseColor.rgb, _AccentColor.rgb, bands * .46) * light;
            }
            if (_Caustics > 0)
            {
                float caustic = abs(sin(p.x * 2.2 + sin(p.z * .9 + _MotionTime * .4)) + sin(p.z * 2 + sin(p.x + _MotionTime * .3)));
                color += half3(.075,.18,.17) * pow(saturate(1-caustic), 14) * _Caustics * saturate(normal.y * .5 + .5);
            }
            float haze = 1 - exp(-max(0, distance(GetCameraPositionWS(),p) - 12) * .010);
            color = lerp(color, _HazeColor.rgb, haze * .68);
            float edge = smoothstep(5.4, 8.0, abs(p.x));
            color += _ChorusColor.rgb * _Chorus * edge * (.10 + _Emission * .30);
            return half4(color,1);
        }
        ENDHLSL
        Pass
        {
            Name "Scenic2DRenderer"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
        Pass
        {
            Name "ScenicForwardRenderer"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
