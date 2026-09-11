Shader "Saber/Obsidian Stage Metal"
{
    Properties
    {
        _BaseColor ("Surface", Color) = (0.06, 0.08, 0.10, 1)
        _EmissionColor ("Inlay", Color) = (0, 0, 0, 1)
        _Smoothness ("Satin finish", Range(0,1)) = 0.35
        _Metallic ("Metal", Range(0,1)) = 0.32
        _MotionStyle ("Rigid panels: lift 1 hinge 2 prism 3", Float) = 0
        _MotionTime ("Song clock", Float) = 0
        _FloorY ("Floor height", Float) = -2.5
        _Chorus ("Musical section", Range(0,1)) = 0
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
            float _MotionStyle, _MotionTime, _FloorY, _Chorus;
        CBUFFER_END
        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 motion : TEXCOORD1;
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
            float3 p = input.positionOS.xyz;
            float3 n = input.normalOS;
            float3 anchor = input.motion.xyz;
            float side = sign(anchor.x);
            if (input.motion.w > .5 && _MotionStyle > .5)
            {
                // 可動建築の「移動→静止→復帰」を、通路の奥行きごとにずらす。
                // 波形をただ往復させず、板の重さを感じる短い停止時間を作る。
                float cycle = frac(_MotionTime / (_MotionStyle < 1.5 ? 6.8 : 8.4) - anchor.z * .027 + side * .19);
                float wave = smoothstep(.04,.27,cycle) * (1-smoothstep(.53,.83,cycle));
                if (_MotionStyle < 1.5 && abs(anchor.x) > 3)
                {
                    // 中央二列・固定レーンは残し、左右の板を順番に持ち上げる。
                    p.y += wave * (.24 + _Chorus * .12);
                }
                else if (_MotionStyle > 1.5 && _MotionStyle < 2.5)
                {
                    float angle = side * wave * (.045 + _Chorus * .022);
                    float2 pivot = float2(side * .045, _FloorY - .018);
                    float2 q = p.xy - pivot;
                    float s = sin(angle), c = cos(angle);
                    p.xy = pivot + float2(c*q.x-s*q.y, s*q.x+c*q.y);
                    n.xy = float2(c*n.x-s*n.y, s*n.x+c*n.y);
                }
                else if (_MotionStyle > 2.5)
                {
                    // 研究所の壁板が独立浮遊し、見せ場では外側へ開く。
                    float phase = _MotionTime * .76 - anchor.z * .23 + anchor.y * 1.4;
                    float delay = clamp(anchor.z*.006 + (anchor.y-_FloorY)*.026,0,.34);
                    float open = smoothstep(delay,delay+.66,_Chorus);
                    p.x += side * (.20 + .18 * sin(phase) + open * .55);
                    p.y += .25 * sin(phase + 1.1) + open * (anchor.y - _FloorY - 2.6) * .16;
                    p.z += .18 * cos(phase * .83);
                }
            }
            VertexPositionInputs position = GetVertexPositionInputs(p);
            output.positionCS = position.positionCS;
            output.positionWS = position.positionWS;
            output.normalWS = TransformObjectToWorldNormal(n);
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
            float edge = smoothstep(4.8, 7.0, abs(input.positionWS.x));
            float sweep = pow(saturate(.5 + .5 * sin(input.positionWS.z * .48 - _MotionTime * 1.4)), 5);
            color += edge * _Chorus * (_BaseColor.rgb * .20 + _EmissionColor.rgb * (.30 + sweep * .55));
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
