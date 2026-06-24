Shader "TransparentSights/ChangeColor"
{
    Properties
    {
        _MainTex ("Color", 2D) = "white" {}
		_ColorMultiplier ("Color Multiplier", Range(0, 1)) = 0.25
		_Alpha ("Alpha", Range(0, 1)) = 0.25
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Cull Off
            ZClip False
            ZTest Always
            ZWrite Off

            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

	        sampler2D _MainTex;
			float _ColorMultiplier;
			float _Alpha;

            v2f vert (appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float4 color = tex2D(_MainTex, i.uv);
                return float4(color.rgb * _ColorMultiplier, _Alpha);
			}

            ENDCG
        }
    }
}
