struct Out {float4 pos:SV_POSITION; float2 uv:TEXCOORD;}; Out Vertex(uint id:SV_VertexID) { Out o; o.uv=float2((id<<1)&2,id&2); o.pos=float4(o.uv.x*2-1,1-o.uv.y*2,0,1); return o; }

Texture2D image:register(t0); Texture2D chroma:register(t1); SamplerState linearClamp:register(s0);
cbuffer Parameters:register(b0) { float4 p; float4 flags; float4 aiFootprint; };
float3 color(float2 uv, bool nv12) {
 float3 c=image.Sample(linearClamp,uv).rgb;
 if(nv12) { float y=c.r; float2 cc=chroma.Sample(linearClamp,uv).rg-128.0/255.0;
  if(flags.x<0.5) y=(y-16.0/255.0)*255.0/219.0;
  float gain=flags.x<0.5?255.0/224.0:1;
  float3 k=flags.y>0.5?float3(1.402,0.344136,0.714136):float3(1.5748,0.187324,0.468124);
  float blue=flags.y>0.5?1.772:1.8556;
  c=float3(y+k.x*cc.y*gain,y-(k.y*cc.x+k.z*cc.y)*gain,y+blue*cc.x*gain); }
 return saturate(c);
}
float luma(float3 rgb) { return dot(rgb, float3(.2126,.7152,.0722)); }
float edgeLuma(float2 uv, bool nv12) {
 float3 sampleColor=image.Sample(linearClamp,uv).rgb;
 return nv12 ? sampleColor.r : luma(sampleColor);
}
#ifdef AI_RESAMPLING
// Split a pixel's footprint at the nearest texel boundary. Each side covers
// at most two texels, so hardware bilinear sampling can integrate both sides.
void areaAxis(float center,float width,out float2 samples,out float weight) {
 float split=clamp(floor(center+.5),center-width*.5,center+width*.5);
 float left=split-(center-width*.5), right=width-left;
 samples=float2(split-.5-max(0,left-1)/max(left,.001), split+.5+max(0,right-1)/max(right,.001));
 weight=right/width;
}
float3 aiTap(float2 uv) { return image.SampleLevel(linearClamp,uv,0).rgb; }
float3 resampleAi(float2 uv) {
 float2 footprint=aiFootprint.xy;
 float3 result=0;
 if (max(footprint.x,footprint.y)>3) {
  float2 step=footprint/(3*p.xy);
  [unroll] for (int y=-1;y<=1;y++)
   [unroll] for (int x=-1;x<=1;x++)
    result+=aiTap(uv+float2(x,y)*step)*(2-abs(x))*(2-abs(y));
  result/=16;
 } else if (max(footprint.x,footprint.y)>1.10) {
  float2 x,y; float wx,wy;
  areaAxis(uv.x*p.x,max(1,footprint.x),x,wx);
  areaAxis(uv.y*p.y,max(1,footprint.y),y,wy);
  float3 top=lerp(aiTap(float2(x.x,y.x)/p.xy),aiTap(float2(x.y,y.x)/p.xy),wx);
  float3 bottom=lerp(aiTap(float2(x.x,y.y)/p.xy),aiTap(float2(x.y,y.y)/p.xy),wx);
  result=lerp(top,bottom,wy);
 } else {
  // Preserve local contrast while staying within the same four texels.
  float2 texel=uv*p.xy-.5, phase=frac(texel);
  phase=lerp(phase,phase*phase*(3-2*phase),.55);
  result=aiTap((floor(texel)+phase+.5)/p.xy);
 }
 return result;
}
float3 filterAi(float2 uv, float3 center) {
 // One AI texel is enough to follow diagonal edges without averaging away
 // the detail recovered between the original capture pixels.
 float4 neighbors = float4(
  luma(image.SampleLevel(linearClamp, uv, 0, int2(0,-1)).rgb),
  luma(image.SampleLevel(linearClamp, uv, 0, int2(0, 1)).rgb),
  luma(image.SampleLevel(linearClamp, uv, 0, int2(1, 0)).rgb),
  luma(image.SampleLevel(linearClamp, uv, 0, int2(-1,0)).rgb));
 float y = luma(center);
 float low = min(y, min(min(neighbors.x, neighbors.y), min(neighbors.z, neighbors.w)));
 float high = max(y, max(max(neighbors.x, neighbors.y), max(neighbors.z, neighbors.w)));
 float range = high - low;
 float2 gradient = float2(neighbors.z - neighbors.w, neighbors.y - neighbors.x);
 float diagonal = min(abs(gradient.x), abs(gradient.y));
 float reduction = max(1, max(aiFootprint.x, aiFootprint.y));
 float edgeBlend = 0;
 [branch] if (flags.z > 0 && diagonal > max(.035, range * .20)) {
  float2 tangent = float2(-gradient.y, gradient.x) / max(abs(gradient.x), abs(gradient.y));
  float2 step = tangent / p.xy;
  edgeBlend = .32 * smoothstep(.035, .16, diagonal) / reduction;
  center = lerp(center, (aiTap(uv - step) + aiTap(uv + step)) * .5, edgeBlend);
 }

 // Sharpen luma within the measured neighborhood so dark and bright
 // outlines cannot overshoot the values already in the AI output.
 float filtered = luma(center);
 // Leave strong edges from the model intact. Spend the sharpening range
 // on lower-contrast texture, where an outline would be less visible.
 float strength = p.z * (1 - smoothstep(.08, .30, range)) * (1 - edgeBlend) / reduction;
 float correction = (filtered - dot(neighbors, .25)) * strength;
 return saturate(center + clamp(correction, min(0, low - filtered), max(0, high - filtered)));
}
#else
float3 filterPreview(float2 uv, bool nv12) {
 float3 c=color(uv,nv12);
 float edgeBlend=0;
 [branch] if(flags.z>0) {
  float2 d=flags.zw;
  float nw=edgeLuma(uv-d,nv12), se=edgeLuma(uv+d,nv12);
  float ne=edgeLuma(uv+float2(d.x,-d.y),nv12), sw=edgeLuma(uv+float2(-d.x,d.y),nv12);
  float lo=min(min(nw,ne),min(sw,se)), hi=max(max(nw,ne),max(sw,se));
  float2 gradient=float2(ne+se-nw-sw,sw+se-nw-ne);
  float diagonal=min(abs(gradient.x),abs(gradient.y));
  // Follow diagonal edges and preserve flat areas and clean straight lines.
  [branch] if(hi-lo>max(.075,hi*.15) && diagonal>.035) {
   float2 tangent=float2(-gradient.y,gradient.x)/max(abs(gradient.x),abs(gradient.y));
   float3 along=(color(uv+tangent*d,nv12)+color(uv-tangent*d,nv12))*.5;
   edgeBlend=.65*smoothstep(.035,.16,diagonal);
   c=lerp(c,along,edgeBlend);
  }
 }
 if(p.z>0.001) {
  float2 d=1/p.xy;
  float3 n=color(uv+float2(0,-d.y),nv12),s=color(uv+float2(0,d.y),nv12);
  float3 e=color(uv+float2(d.x,0),nv12),w=color(uv+float2(-d.x,0),nv12);
  c=clamp(c+(c-(n+s+e+w)*.25)*p.z*(1-edgeBlend),min(c,min(min(n,s),min(e,w))),max(c,max(max(n,s),max(e,w))));
 }
 return c;
}
#endif

float4 Preview(float4 pos:SV_POSITION,float2 uv:TEXCOORD):SV_TARGET {
#ifdef AI_RESAMPLING
 float3 aiColor;
 [branch] if (aiFootprint.x > 0) aiColor = resampleAi(uv);
 else aiColor = aiTap(uv);
 [branch] if (flags.z > 0 || p.z > .001) aiColor = filterAi(uv, aiColor);
 return float4(aiColor, 1);
#else
 // Plain draws also serve the RGB conversion used by motion analysis.
 [branch] if (flags.z <= 0 && p.z <= .001) return float4(color(uv, p.w > .5), 1);
 // Choose the source layout once, outside the color and edge sampling taps.
 [branch] if (p.w > .5) return float4(filterPreview(uv, true), 1);
 return float4(filterPreview(uv, false), 1);
#endif
}
