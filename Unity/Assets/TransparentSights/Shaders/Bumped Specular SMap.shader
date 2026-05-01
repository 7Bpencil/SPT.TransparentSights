Shader "TransparentSights/p0/Reflective/Bumped Specular SMap" {
	Properties {
		_StencilRef ("Stencil Ref", Float) = 2
		_Color ("Main Color", Color) = (1,1,1,1)
		_BaseTintColor ("Tint Color", Color) = (1,1,1,1)
		_SpecMap ("GlossMap", 2D) = "white" {}
		_SpecColor ("Specular Color", Color) = (0.5,0.5,0.5,1)
		_Glossness ("Specularness", Range(0.01, 10)) = 1
		_Specularness ("Glossness", Range(0.01, 10)) = 0.078125
		_ReflectColor ("Reflection Color", Color) = (1,1,1,0.5)
		_MainTex ("Base (RGB) Specular (A)", 2D) = "white" {}
		[Toggle(TINTMASK)] _HasTint ("Has tint", Float) = 0
		_TintMask ("Tint mask", 2D) = "black" {}
		_Cube ("Reflection Cubemap", Cube) = "" {}
		_BumpMap ("Normalmap", 2D) = "bump" {}
		_SpecVals ("Specular Vals", Vector) = (1.1,2,0,0)
		_DefVals ("Defuse Vals", Vector) = (0.5,0.7,0,0)
		_BumpTiling ("_BumpTiling", Float) = 1
		_NormalIntensity ("Normal intensity", Float) = 1
		_NormalUVMultiplier ("Normal UV tiling", Float) = 1
		_Factor ("Z Offset Angle", Float) = 0
		_Units ("Z Offset Forward", Float) = 0
		_DropsSpec ("Drops spec", Float) = 128
		_Temperature ("_Temperature", Vector) = (0.1,0.2,0.28,0)
		[Space(30)] [Header(Wetting)] _RippleTexScale ("_RippleTexScale", Float) = 4
		_RippleFakeLightIntensityOffset ("Ripple fake light offset", Float) = 0.7
		_NightRippleFakeLightOffset ("Night fake light offset", Float) = 0.2
		_NdotLOffset ("Normal dot light offset", Float) = 0.4
		[Toggle(USERAIN)] _USERAIN ("Is material affected by rain", Float) = 0
		[HideInInspector] _SkinnedMeshMaterial ("Skinned Mesh Material", Float) = 0
		[Toggle(USEHEAT)] USEHEAT ("Use metal heat glow", Float) = 0
		_HeatVisible ("_HeatVisible([0-1] for thermalVision only)", Float) = 1
		[HDR] _HeatColor1 ("_HeatColor1", Color) = (1,0,0,1)
		[HDR] _HeatColor2 ("_HeatColor2", Color) = (1,0.34,0,1)
		_HeatCenter ("_HeatCenter", Vector) = (0,0,0,1)
		_HeatSize ("_HeatSize", Vector) = (0.02,0.04,0.02,1)
		_HeatTemp ("_HeatTemp", Float) = 0
	}
	SubShader {
		Pass {
			Name "DEPTH_PREPASS"
			Tags { "LIGHTMODE" = "ForwardBase" "RenderType" = "Transparent" "Queue" = "Transparent" }
			ColorMask 0
		    Blend SrcAlpha OneMinusSrcAlpha
			Stencil {
				Ref [_StencilRef]
				WriteMask 63
				Comp Always
				Pass Replace
			}

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
            #pragma multi_compile_fwdbase

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

			float4 vert(float4 vertex : POSITION) : SV_POSITION
			{
                return UnityObjectToClipPos(vertex);
 			}

			float4 frag() : SV_Target
			{
                return 0;
			}
			ENDCG
		}
		Pass {
			Name "FORWARD"
			Tags { "LIGHTMODE" = "ForwardBase" "RenderType" = "Transparent" "Queue" = "Transparent" }
		    Blend SrcAlpha OneMinusSrcAlpha
			ZWrite Off
			ZTest Equal
			Stencil {
				Ref [_StencilRef]
				WriteMask 63
				Comp Always
				Pass Replace
			}

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
            #pragma multi_compile_fwdbase

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

			struct v2f
			{
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 viewDirWS : TEXCOORD2;
                float3 worldPos : TEXCOORD3;
			};

			float4 _MainTex_ST;
			float4 _Color;
			float4 _ReflectColor;
			float _Specularness;
			float _Glossness;
			float _NormalIntensity;
			float _NormalUVMultiplier;
			float3 _SpecVals;
			float3 _DefVals;
			float _BumpTiling;
			float3 _Temperature;
			float _ThermalVisionOn;
			float _HeatThermalFactor;
			sampler2D _MainTex;
			sampler2D _SpecMap;
			sampler2D _BumpMap;
			samplerCUBE _Cube;

			v2f vert(appdata_full v)
			{
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);

                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normalWS = UnityObjectToWorldNormal(v.normal);
                o.viewDirWS = _WorldSpaceCameraPos - o.worldPos;

                return o;
 			}

			float4 frag(v2f i) : SV_Target
			{
                float3 N = normalize(i.normalWS);
                float3 V = normalize(i.viewDirWS);

                float4 tex = tex2D(_MainTex, i.uv);
                float3 albedo = tex.rgb * _Color.rgb;

                float3 ambient = UNITY_LIGHTMODEL_AMBIENT.rgb * albedo;

                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float NdotL = saturate(dot(N, L));
                float3 diffuse = albedo * _LightColor0.rgb * NdotL;

                float fresnel = 1.0 - saturate(dot(N, V));
                fresnel = fresnel * fresnel * 0.5;

                float specMask = tex2D(_SpecMap, i.uv).r * _Specularness;
                float gloss = tex.a * _Glossness;

                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), gloss * 128.0);

                float specFactor = (_SpecVals.y * fresnel + _SpecVals.x) * 0.5;
                float3 specular = _SpecColor.rgb * spec * specMask * specFactor;

                float3 finalColor = ambient + diffuse + specular;

                return float4(finalColor, _Color.a);
			}
			ENDCG
		}
        Pass {
            Name "FORWARD_ADD"
            Tags { "LIGHTMODE"="ForwardAdd" }

            ZWrite Off
            ZTest LEqual
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragAdd
            #pragma multi_compile_fwdadd

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

			float4 _MainTex_ST;
			float4 _Color;
			float4 _ReflectColor;
			float _Specularness;
			float _Glossness;
			float _NormalIntensity;
			float _NormalUVMultiplier;
			float3 _SpecVals;
			float3 _DefVals;
			float _BumpTiling;
			float3 _Temperature;
			float _ThermalVisionOn;
			float _HeatThermalFactor;
			sampler2D _MainTex;
			sampler2D _SpecMap;
			sampler2D _BumpMap;
			samplerCUBE _Cube;

			struct v2f
			{
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 viewDirWS : TEXCOORD2;
                float3 worldPos : TEXCOORD3;
			};

			v2f vert(appdata_full v)
			{
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);

                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normalWS = UnityObjectToWorldNormal(v.normal);
                o.viewDirWS = _WorldSpaceCameraPos - o.worldPos;

                return o;
 			}

            float4 fragAdd(v2f i) : SV_Target
            {
                float3 N = normalize(i.normalWS);
                float3 V = normalize(i.viewDirWS);

                float3 lightPos = _WorldSpaceLightPos0.xyz;
                float3 L = normalize(lightPos - i.worldPos);

                float dist = length(lightPos - i.worldPos);
                float atten = 1.0 / (1.0 + dist * dist);

                float4 tex = tex2D(_MainTex, i.uv);
                float3 albedo = tex.rgb * _Color.rgb;

                float NdotL = saturate(dot(N, L));
                float3 diffuse = albedo * _LightColor0.rgb * NdotL * atten;

                float fresnel = 1.0 - saturate(dot(N, V));
                fresnel = fresnel * fresnel * 0.5;

                float specMask = tex2D(_SpecMap, i.uv).r * _Specularness;
                float gloss = tex.a * _Glossness;

                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), gloss * 128.0);

                float specFactor = (_SpecVals.y * fresnel + _SpecVals.x) * 0.5;
                float3 specular = _SpecColor.rgb * spec * specMask * specFactor * atten;

                return float4(diffuse + specular, 0);
            }
            ENDCG
        }
	}
}
