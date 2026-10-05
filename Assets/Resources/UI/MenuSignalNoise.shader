Shader "Saber/UI/MenuSignalNoise"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _NoiseStrength ("Strength", Range(0,1)) = 0
        _NoiseTime ("Time", Float) = 0
        _Overlay ("Screen overlay", Float) = 0
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        CGINCLUDE
        #include "UnityCG.cginc"
        #include "UnityUI.cginc"
        struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
        struct v2f { float4 vertex : SV_POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; float4 local : TEXCOORD1; float4 screen : TEXCOORD2; };
        sampler2D _MainTex;
        float4 _Color, _TextureSampleAdd, _ClipRect;
        float _NoiseStrength, _NoiseTime, _Overlay;
        float Hash(float2 p)
        {
            float3 q = frac(float3(p.xyx) * .1031);
            q += dot(q, q.yzx + 33.33);
            return frac((q.x + q.y) * q.z);
        }
        // 約2.8秒に一度、0.24秒の短い乱れ。文字を読める間隔を保つ。
        float Burst()
        {
            float phase = fmod(_NoiseTime + 1.8, 2.8);
            return smoothstep(0, .025, phase) * (1 - smoothstep(.16, .24, phase));
        }
        v2f Vert(appdata v)
        {
            v2f o;
            o.local = v.vertex;
            o.vertex = UnityObjectToClipPos(v.vertex);
            o.color = v.color * _Color;
            o.uv = v.uv;
            o.screen = ComputeScreenPos(o.vertex);
            return o;
        }
        float Mask(v2f i)
        {
            #ifdef UNITY_UI_CLIP_RECT
            return UnityGet2DClipping(i.local.xy, _ClipRect);
            #else
            return 1;
            #endif
        }
        fixed4 Frag(v2f i) : SV_Target
        {
            float2 pixel = i.screen.xy / i.screen.w * _ScreenParams.xy;
            // 1080pで約2pxの粒。動画や縮小表示で1pxの粒が消えないようにする。
            float2 signalPixel = pixel * (1080 / _ScreenParams.y);
            float tick = floor(_NoiseTime * 15);
            float grain = Hash(floor(signalPixel / 2.2) + float2(tick * 71, tick * 29));
            float scan = 1 - step(1.2, fmod(signalPixel.y + floor(_NoiseTime * 9), 7));
            float row = floor(signalPixel.y / 4);
            float band = step(lerp(.966, .986, _Overlay), Hash(float2(row, floor(_NoiseTime * 20))));
            float glitch = Burst() * band * _NoiseStrength;
            fixed4 c = (tex2D(_MainTex, i.uv) + _TextureSampleAdd) * i.color;
            if (_Overlay > .5)
            {
                // HARDは粗さの違う粒を重ね、縮小した画面でも強い砂嵐に見せる。
                // タイトルの粒・発生周期とは独立させる。
                float hardTick = floor(_NoiseTime * 20);
                float fine = Hash(floor(signalPixel / 3.2) + float2(hardTick * 71, hardTick * 29));
                float coarse = Hash(floor(signalPixel / 7) + float2(hardTick * 37, hardTick * 83));
                float hardGrain = lerp(fine, coarse, .3);
                float white = step(.5, hardGrain);
                c.rgb = white.xxx;
                float grainAlpha = abs(hardGrain - .5) * .22 + scan * .014;

                // 約1.6秒ごとの色付き横線と、常時ゆっくり流れる途切れた横帯。
                float hardPhase = fmod(_NoiseTime + .65, 1.6);
                float hardBurst = smoothstep(0, .025, hardPhase) * (1 - smoothstep(.22, .34, hardPhase));
                float hardRow = floor(signalPixel.y / 6);
                float tear = step(.948, Hash(float2(hardRow, hardTick))) * hardBurst;
                float trackingY = frac(_NoiseTime * .23) * 1080;
                float tracking = 1 - smoothstep(8, 42, abs(signalPixel.y - trackingY));
                float broken = step(.32, Hash(float2(floor(signalPixel.x / 16), hardRow) + hardTick));
                float interference = tear * .18 + tracking * broken * .065;
                float3 tint = lerp(float3(1,.12,.27), float3(.12,.9,1), step(.5, Hash(float2(hardRow, hardTick))));
                c.rgb = lerp(c.rgb, tint, saturate(interference * 7));
                c.a *= _NoiseStrength * saturate(grainAlpha + interference);
            }
            else
            {
                c.rgb *= 1 - _NoiseStrength * ((1 - grain) * .27 + scan * .065);
                c.rgb = lerp(c.rgb, c.brg, glitch * .55);
            }
            c.a *= Mask(i);
            #ifdef UNITY_UI_ALPHACLIP
            clip(c.a - .001);
            #endif
            return c;
        }
        ENDCG
        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            ENDCG
        }
    }
}
