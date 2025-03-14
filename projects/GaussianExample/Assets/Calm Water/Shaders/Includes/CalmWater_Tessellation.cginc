
#ifdef DX11
#ifdef UNITY_CAN_COMPILE_TESSELLATION
struct TessVertex {
    float4 vertex 	: INTERNALTESSPOS;
    float3 normal 	: NORMAL;
    float4 tangent 	: TANGENT;
    float2 texcoord : TEXCOORD0;
#ifdef UNITY_PASS_FORWARDADD
    float2 texcoord1 : TEXCOORD1;
#endif
    //float4 color 	: COLOR;
};

struct OutputPatchConstant {
    float edge[3]         : SV_TessFactor;
    float inside : SV_InsideTessFactor;
};
TessVertex tessvert(appdata v) {
    TessVertex o;
    o.vertex = v.vertex;
    o.normal = v.normal;
    o.tangent = v.tangent;
    o.texcoord = v.texcoord;
#ifdef UNITY_PASS_FORWARDADD
    o.texcoord1 = v.texcoord1;
#endif
    //o.color 	= v.color;
    return o;
}

float4 Tessellation(TessVertex v, TessVertex v1, TessVertex v2) {
    return UnityEdgeLengthBasedTess(v.vertex, v1.vertex, v2.vertex, 32 - _Tess);
}

OutputPatchConstant hullconst(InputPatch<TessVertex, 3> v) {
    OutputPatchConstant o;
    float4 ts = Tessellation(v[0], v[1], v[2]);
    o.edge[0] = ts.x;
    o.edge[1] = ts.y;
    o.edge[2] = ts.z;
    o.inside = ts.w;
    return o;
}

[domain("tri")]
[partitioning("fractional_odd")]
[outputtopology("triangle_cw")]
[patchconstantfunc("hullconst")]
[outputcontrolpoints(3)]
TessVertex hs_surf(InputPatch<TessVertex, 3> v, uint id : SV_OutputControlPointID) {
    return v[id];
}

[domain("tri")]
v2f ds_surf(OutputPatchConstant tessFactors, const OutputPatch<TessVertex, 3> vi, float3 bary : SV_DomainLocation) {
    appdata v = (appdata)0;

    v.vertex = vi[0].vertex * bary.x + vi[1].vertex * bary.y + vi[2].vertex * bary.z;
    v.texcoord = vi[0].texcoord * bary.x + vi[1].texcoord * bary.y + vi[2].texcoord * bary.z;
#ifdef UNITY_PASS_FORWARDADD
    v.texcoord1 = vi[0].texcoord1 * bary.x + vi[1].texcoord1 * bary.y + vi[2].texcoord1 * bary.z;
#endif
    //v.color 	= vi[0].color*bary.x 	+ vi[1].color*bary.y 	+ vi[2].color*bary.z;
    v.tangent = vi[0].tangent * bary.x + vi[1].tangent * bary.y + vi[2].tangent * bary.z;
    v.normal = vi[0].normal * bary.x + vi[1].normal * bary.y + vi[2].normal * bary.z;

    v2f o = vert(v);

    return o;
}

#endif
#endif // End DX11