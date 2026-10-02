Shader "Saber/PhotographicBackdrop"
{
    Properties
    {
        _BackgroundTex ("Photographic landscape", 2D) = "black" {}
        _Style ("Aurora / Rain / Ocean", Float) = 0
        _MotionTime ("Song clock", Float) = 0
        _Chorus ("Musical intensity", Range(0,1)) = 0
        _Effects ("Effects strength", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Background" }
        ZWrite Off
        ZTest Always
        Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_BackgroundTex);
        SAMPLER(sampler_BackgroundTex);
        CBUFFER_START(UnityPerMaterial)
            float4 _BackgroundTex_TexelSize;
            float _Style, _MotionTime, _Chorus, _Effects;
        CBUFFER_END
        struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionCS = float4(input.positionOS.xy, UNITY_RAW_FAR_CLIP_VALUE, 1);
            output.positionCS.y *= _ProjectionParams.x;
            output.uv = input.uv;
            return output;
        }
        float Hash(float2 p) { return frac(sin(dot(p,float2(127.1,311.7))) * 43758.5453); }
        float Rain(float2 uv, float t, float scale)
        {
            float2 grid = float2((uv.x + uv.y * .065) * scale, uv.y * 22 + t * 15);
            float2 cell = floor(grid), f = frac(grid);
            float seed = Hash(cell);
            float streak = (1-smoothstep(.014,.055,abs(f.x-.2-seed*.6))) * (1-smoothstep(.04,.8,f.y));
            return streak * step(.71,seed);
        }
        half4 Frag(Varyings input) : SV_Target
        {
            float2 screenUV = input.uv;
            float2 uv = screenUV;
            // 縦横比を保って画面を覆う。ワイド画面でも引き伸ばさない。
            float imageAspect = _BackgroundTex_TexelSize.z / _BackgroundTex_TexelSize.w;
            float screenAspect = _ScreenParams.x / _ScreenParams.y;
            uv = (uv-.5) * float2(min(1,screenAspect/imageAspect),min(1,imageAspect/screenAspect)) + .5;
            float t = _MotionTime;
            half3 original = SAMPLE_TEXTURE2D(_BackgroundTex,sampler_BackgroundTex,uv).rgb;
            float edge = smoothstep(.14,.48,abs(screenUV.x-.5));
            half3 color;
            if (_Style < .5)
            {
                float sky = smoothstep(.36,.51,uv.y);
                float aurora = saturate((original.g-max(original.r,original.b)*.85)*5) * sky;
                float lake = 1-smoothstep(.30,.36,uv.y);
                float2 drift = float2(sin(uv.y*13+t*.23),cos(uv.x*11+t*.19)) * .008 * aurora;
                drift.x += sin(uv.y*240+t*1.05+sin(uv.x*18))*.0018*lake;
                color = SAMPLE_TEXTURE2D(_BackgroundTex,sampler_BackgroundTex,clamp(uv+drift,.001,.999)).rgb;
                color *= 1 + aurora*(.10*sin(t*.52+uv.x*10)+_Chorus*.16)*_Effects;
            }
            else if (_Style < 1.5)
            {
                float road = 1-smoothstep(.38,.51,uv.y);
                float2 drift = float2(sin(uv.y*310+t*2)*.0008*road,0);
                color = SAMPLE_TEXTURE2D(_BackgroundTex,sampler_BackgroundTex,clamp(uv+drift,.001,.999)).rgb;
                float rain = Rain(screenUV,t,160) + Rain(screenUV+float2(.41,.17),t*.78,240)*.5;
                color += half3(.30,.39,.43)*rain*.14*(.16+.84*edge)*_Effects;
                // 街全体を点滅させず、濡れた路面の光だけを僅かに強める。
                color *= 1 + road*edge*_Chorus*.07*_Effects;
            }
            else
            {
                float water = smoothstep(.15,.9,uv.y);
                float2 drift = float2(sin(uv.y*16+t*.36),sin(uv.x*21+t*.27))*.0035*water;
                color = SAMPLE_TEXTURE2D(_BackgroundTex,sampler_BackgroundTex,clamp(uv+drift,.001,.999)).rgb;
                float rays = pow(saturate(sin((uv.x-.5)/(uv.y+.35)*43+t*.18)),12);
                color += half3(.015,.055,.067)*rays*water*edge*(.65+_Chorus*.3)*_Effects;
                color *= 1 + sin(uv.x*14+uv.y*11+t*.55)*.035*water;
            }
            // 中央には柔らかい減光。枠状のマスクを出さず、写真の階調を維持する。
            float focus = exp(-pow(abs((screenUV.x-.5)*3.3),4)-pow(abs((screenUV.y-.48)*3.1),4));
            color *= .90 - focus*.20;
            // 上隅のスコアと最大コンボは、明るい水面やオーロラから背景側で保護する。
            float hudCorners = smoothstep(.76,.91,screenUV.y) * (1-smoothstep(.18,.40,min(screenUV.x,1-screenUV.x)));
            color *= 1-hudCorners*.87;
            return half4(color,1);
        }
        ENDHLSL
        Pass
        {
            Name "Photographic2D"
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
        Pass
        {
            Name "PhotographicForward"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
