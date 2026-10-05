Shader "Saber/UI/HardIntroNoise"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Strength ("Strength", Range(0,1)) = 0
        _Clock ("Clock", Float) = 0
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off ZWrite Off ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            struct appdata { float4 vertex:POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; };
            struct v2f { float4 vertex:SV_POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; float2 local:TEXCOORD1; };
            float4 _ClipRect; float _Strength,_Clock;
            v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.local=v.vertex.xy; o.color=v.color; o.uv=v.uv; return o; }
            float hash(float2 p) { float3 q=frac(float3(p.xyx)*.1031); q+=dot(q,q.yzx+33.33); return frac((q.x+q.y)*q.z); }
            fixed4 frag(v2f i):SV_Target
            {
                // 720pで2pxの粒、18Hz。写真中も粒が消えない密度を保つ。
                float2 pixel=i.uv*float2(1280,720);
                float tick=floor(_Clock*18);
                float grain=hash(floor(pixel/2)+float2(tick*71,tick*29));
                float roll=fmod(_Clock*69,720);
                float scan=1-step(1,fmod(pixel.y,4));
                float band=step(roll,pixel.y)*(1-step(roll+35+_Strength*30,pixel.y));
                grain*=1-.22*saturate(scan+band);
                float4 c=float4(grain.xxx,saturate(_Strength)*i.color.a);
                #ifndef UNITY_COLORSPACE_GAMMA
                c.rgb=GammaToLinearSpace(c.rgb);
                #endif
                #ifdef UNITY_UI_CLIP_RECT
                c.a*=UnityGet2DClipping(i.local,_ClipRect);
                #endif
                return c;
            }
            ENDCG
        }
    }
}
