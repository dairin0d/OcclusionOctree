Shader "Custom/ColorDepthTextureShader" {
	Properties {
		_MainTex ("Base (RGB)", 2D) = "white" {}
	}
	SubShader {
		Tags { "RenderType"="Opaque" }
		LOD 200
		
		Cull Off
		
		Blend Off
		
		//ZWrite On
		//ZTest LEqual
		
		//ZWrite Off
		ZWrite On
		ZTest Always
		
		Lighting Off
		Fog { Mode Off }
		
		Pass {
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			
			#pragma multi_compile ORTHOGRAPHIC PERSPECTIVE
			
			#include "UnityCG.cginc"
			
			sampler2D _MainTex;
			uniform float4 _MainTex_ST;
			
			struct appdata {
				float4 vertex : POSITION;
				float2 texcoord0 : TEXCOORD0;
			};
			
			struct v2f {
				float4 pos : SV_POSITION;
				float2 texcoord0 : TEXCOORD0;
			};
			
			v2f vert(appdata v) {
				v2f o;
				o.pos = float4(mul(UNITY_MATRIX_MVP, v.vertex).xy, 1, 1);
				o.texcoord0 = TRANSFORM_TEX(v.texcoord0, _MainTex);
				return o;
			}
			
			fixed4 frag(v2f i) : COLOR0 {
				fixed4 texcol = tex2D(_MainTex, i.texcoord0);
				return texcol;
			}
			ENDCG
		}
	} 
	FallBack "Diffuse"
}
