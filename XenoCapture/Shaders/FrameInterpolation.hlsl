// Xeno Capture: bounded, bidirectional block matching for video-only interpolation.
// No neural model, engine motion vectors, or depth buffer is used.
cbuffer Parameters : register(b0) {
    float2 SourceSize; float2 AnalysisSize;
    float2 FlowSize; float Alpha; uint Flags;
};
Texture2D<float4> A : register(t0);
Texture2D<float4> B : register(t1);
Texture2D<float4> Forward : register(t2);
Texture2D<float4> Backward : register(t3);
Texture2D<int2> RawForward : register(t0);
Texture2D<int2> RawBackward : register(t1);
Texture2D<uint> ForwardCost : register(t2);
Texture2D<uint> BackwardCost : register(t3);
SamplerState LinearClamp : register(s0);
RWTexture2D<float4> Output0 : register(u0);
RWTexture2D<float4> Output1 : register(u1);
RWStructuredBuffer<uint> Stats : register(u2);
float Y(float3 c) { return dot(c,float3(.2126,.7152,.0722)); }
float Max3(float3 c) { return max(c.x,max(c.y,c.z)); }
[numthreads(16,8,1)] void Downsample(uint3 id:SV_DispatchThreadID) {
    if(any(id.xy >= uint2(AnalysisSize))) return;
    float2 uv=(id.xy+.5)/AnalysisSize, q=.25/AnalysisSize;
    float4 c=A.SampleLevel(LinearClamp,uv+float2(-q.x,-q.y),0)
             +A.SampleLevel(LinearClamp,uv+float2(q.x,-q.y),0)
             +A.SampleLevel(LinearClamp,uv+float2(-q.x,q.y),0)
             +A.SampleLevel(LinearClamp,uv+q,0);
    Output0[id.xy]=float4(c.rgb*.25,1);
}
groupshared uint3 CompareTotals;
[numthreads(16,8,1)] void Compare(uint3 id:SV_DispatchThreadID,uint3 group:SV_GroupID,uint thread:SV_GroupIndex) {
    if (thread == 0) CompareTotals = 0;
    GroupMemoryBarrierWithGroupSync();
    if (all(id.xy < uint2(AnalysisSize))) {
        float d = Max3(abs(A.Load(int3(id.xy,0)).rgb - B.Load(int3(id.xy,0)).rgb));
        if (d > .008) InterlockedAdd(CompareTotals.x, 1);
        if (d > .20) InterlockedAdd(CompareTotals.y, 1);
        InterlockedAdd(CompareTotals.z, uint(min(d,1)*4095+.5));
    }
    GroupMemoryBarrierWithGroupSync();
    if (thread == 0) {
        uint2 extent = min(uint2(16,8), uint2(AnalysisSize) - group.xy * uint2(16,8));
        InterlockedAdd(Stats[0], CompareTotals.x);
        InterlockedAdd(Stats[1], CompareTotals.y);
        InterlockedAdd(Stats[2], CompareTotals.z);
        InterlockedAdd(Stats[3], extent.x * extent.y);
    }
}
float Cost(float2 p,float2 d,bool reverse) {
    float error=0;
    [unroll] for(int y=-1;y<=1;y++) [unroll] for(int x=-1;x<=1;x++) {
        float2 uv=(p+float2(x,y)*2+.5)/AnalysisSize;
        float3 a=reverse ? B.SampleLevel(LinearClamp,uv,0).rgb : A.SampleLevel(LinearClamp,uv,0).rgb;
        float3 b=reverse ? A.SampleLevel(LinearClamp,uv+d/AnalysisSize,0).rgb : B.SampleLevel(LinearClamp,uv+d/AnalysisSize,0).rgb;
        error+=dot(abs(a-b),float3(.333333,.333333,.333333));
    }
    return error/9;
}
groupshared float ScoreF[128], ScoreB[128];
groupshared float2 OffsetF[128], OffsetB[128];
groupshared float2 BaseF, BaseB;
groupshared float InitialF, InitialB;
groupshared uint StaticTile;
void ReduceBest(uint thread) {
    GroupMemoryBarrierWithGroupSync();
    [unroll] for(uint stride=64;stride>0;stride>>=1) {
        if(thread<stride) {
            if(ScoreF[thread+stride]<ScoreF[thread]) { ScoreF[thread]=ScoreF[thread+stride]; OffsetF[thread]=OffsetF[thread+stride]; }
            if(ScoreB[thread+stride]<ScoreB[thread]) { ScoreB[thread]=ScoreB[thread+stride]; OffsetB[thread]=OffsetB[thread+stride]; }
        }
        GroupMemoryBarrierWithGroupSync();
    }
}
float Confidence(float initial,float error,float alternative) {
    return initial-error<.005 ? 0 : saturate((.045-error)/.035)*saturate((alternative-error)/.025);
}
[numthreads(128,1,1)] void Motion(uint3 group:SV_GroupID,uint thread:SV_GroupIndex) {
    float2 p=min(group.xy*8+3.5,AnalysisSize-1);
    if(thread==0) { InitialF=Cost(p,0,false); InitialB=Cost(p,0,true); StaticTile=max(InitialF,InitialB)<.003; }
    GroupMemoryBarrierWithGroupSync();
    if(StaticTile) {
        if(thread==0) { Output0[group.xy]=float4(0,0,1,InitialF); Output1[group.xy]=float4(0,0,1,InitialB); }
        return;
    }
    bool wide=(Flags&1)!=0;
    float2 d=float2(int(thread%9)*2-8,int(thread/9)*2-8);
    OffsetF[thread]=OffsetB[thread]=d;
    ScoreF[thread]=thread<81 ? Cost(p,d,false)+dot(d,d)*.00001 : 1e6;
    ScoreB[thread]=thread<81 ? Cost(p,d,true)+dot(d,d)*.00001 : 1e6;
    // Keep every original small-motion candidate. Add only the outer ring
    // of the wider search; sparse large offsets must not replace small ones.
    if(wide && thread<81 && any(abs(d)>4)) {
        float2 large=d*2;
        float f=Cost(p,large,false)+dot(large,large)*.00001;
        float b=Cost(p,large,true)+dot(large,large)*.00001;
        if(f<ScoreF[thread]) { ScoreF[thread]=f; OffsetF[thread]=large; }
        if(b<ScoreB[thread]) { ScoreB[thread]=b; OffsetB[thread]=large; }
    }
    ReduceBest(thread);
    float step=wide ? 2:1;
    [unroll] for(int level=0;level<5;level++) {
        if(!wide && level==4) break;
        if(thread==0) { BaseF=OffsetF[0]; BaseB=OffsetB[0]; }
        GroupMemoryBarrierWithGroupSync();
        float2 delta=float2(int(thread%3)-1,int(thread/3)-1)*step;
        float2 f=BaseF+delta,b=BaseB+delta;
        OffsetF[thread]=f; OffsetB[thread]=b;
        ScoreF[thread]=thread<9 ? Cost(p,f,false)+dot(f,f)*.00001 : 1e6;
        ScoreB[thread]=thread<9 ? Cost(p,b,true)+dot(b,b)*.00001 : 1e6;
        ReduceBest(thread); step*=.5;
    }
    if(thread==0) { BaseF=OffsetF[0]; BaseB=OffsetB[0]; }
    GroupMemoryBarrierWithGroupSync();
    if(thread==0) {
        float ef=Cost(p,BaseF,false),eb=Cost(p,BaseB,true),af=1,ab=1;
        af=min(af,Cost(p,BaseF+float2(2,0),false)); af=min(af,Cost(p,BaseF-float2(2,0),false));
        af=min(af,Cost(p,BaseF+float2(0,2),false)); af=min(af,Cost(p,BaseF-float2(0,2),false));
        ab=min(ab,Cost(p,BaseB+float2(2,0),true)); ab=min(ab,Cost(p,BaseB-float2(2,0),true));
        ab=min(ab,Cost(p,BaseB+float2(0,2),true)); ab=min(ab,Cost(p,BaseB-float2(0,2),true));
        float cf=Confidence(InitialF,ef,af),cb=Confidence(InitialB,eb,ab);
        Output0[group.xy]=float4(BaseF/AnalysisSize,cf,ef); Output1[group.xy]=float4(BaseB/AnalysisSize,cb,eb);
        if(max(InitialF,InitialB)>.006) { InterlockedAdd(Stats[4],1); if(min(cf,cb)>.45&&max(ef,eb)<.035) InterlockedAdd(Stats[5],1); }
    }
}
groupshared uint HardwareTrusted;
[numthreads(16,8,1)] void Hardware(uint3 id:SV_DispatchThreadID,uint3 group:SV_GroupID,uint thread:SV_GroupIndex) {
    if (thread == 0) HardwareTrusted = 0;
    GroupMemoryBarrierWithGroupSync();
    if (all(id.xy < uint2(FlowSize))) {
        float fc=ForwardCost.Load(int3(id.xy,0))/255.0,bc=BackwardCost.Load(int3(id.xy,0))/255.0;
        Output0[id.xy]=float4(RawForward.Load(int3(id.xy,0))/32.0/SourceSize,saturate(1-fc*4),fc);
        Output1[id.xy]=float4(RawBackward.Load(int3(id.xy,0))/32.0/SourceSize,saturate(1-bc*4),bc);
        if (max(fc,bc) < .2) InterlockedAdd(HardwareTrusted, 1);
    }
    GroupMemoryBarrierWithGroupSync();
    if (thread == 0) {
        uint2 extent = min(uint2(16,8), uint2(FlowSize) - group.xy * uint2(16,8));
        InterlockedAdd(Stats[4], extent.x * extent.y);
        InterlockedAdd(Stats[5], HardwareTrusted);
    }
}
float4 FlowAt(Texture2D<float4> field,float2 uv) {
    int2 p=clamp(int2(uv*FlowSize),int2(0,0),int2(FlowSize)-1);
    return field.Load(int3(p,0));
}
bool StableEdge(float2 uv,float3 a,float3 b) {
    if(Max3(abs(a-b))>.006) return false;
    [unroll] for(int r=1;r<=2;r++) {
        float2 q=float2(r,r)/SourceSize;
        float2 ga=float2(Y(A.SampleLevel(LinearClamp,uv+float2(q.x,0),0).rgb)-Y(A.SampleLevel(LinearClamp,uv-float2(q.x,0),0).rgb),
                         Y(A.SampleLevel(LinearClamp,uv+float2(0,q.y),0).rgb)-Y(A.SampleLevel(LinearClamp,uv-float2(0,q.y),0).rgb));
        float2 gb=float2(Y(B.SampleLevel(LinearClamp,uv+float2(q.x,0),0).rgb)-Y(B.SampleLevel(LinearClamp,uv-float2(q.x,0),0).rgb),
                         Y(B.SampleLevel(LinearClamp,uv+float2(0,q.y),0).rgb)-Y(B.SampleLevel(LinearClamp,uv-float2(0,q.y),0).rgb));
        float aa=dot(ga,ga),bb=dot(gb,gb);
        if(min(aa,bb)>.0144 && dot(ga,gb)>.94*sqrt(aa*bb)) return true;
    }
    return false;
}
bool ValidWarp(float2 uv, float2 source, float2 match, float4 flow, float4 opposite, float phase) {
    float2 pixel = .5 / SourceSize;
    if (any(source < pixel) || any(source > 1 - pixel) || any(match < pixel) || any(match > 1 - pixel))
        return false;
    float2 motion = flow.xy * SourceSize;
    float2 cycleError = (flow.xy + opposite.xy) * SourceSize;
    float2 positionError = (source - uv) * SourceSize + phase * motion;
    float tolerance = 1.0 + .02 * length(motion);
    return min(flow.z, opposite.z) > .2
        && dot(cycleError, cycleError) < tolerance * tolerance
        && dot(positionError, positionError) < .85 * .85;
}

// Predict from the newest image. A backward vector points to where the
// surface was, so it also locates the newest sample for a future pixel.
[numthreads(16,8,1)] void Predict(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= uint2(SourceSize))) return;
    float2 uv = (id.xy + .5) / SourceSize;
    float3 latest = B.Load(int3(id.xy, 0)).rgb;
    float3 result = latest;
    if (Max3(abs(latest - A.Load(int3(id.xy, 0)).rgb)) > .006) {
        float2 source = uv;
        [unroll] for (int i = 0; i < 2; i++)
            source = uv + Alpha * Backward.SampleLevel(LinearClamp, source, 0).xy;
        float4 motion = Backward.SampleLevel(LinearClamp, source, 0);
        float2 match = source + motion.xy;
        float4 opposite = Forward.SampleLevel(LinearClamp, match, 0);
        if (ValidWarp(uv, source, match, motion, opposite, -Alpha)) {
            float3 sample = B.SampleLevel(LinearClamp, source, 0).rgb;
            float3 history = A.SampleLevel(LinearClamp, match, 0).rgb;
            if (Max3(abs(sample - history)) < .065) result = sample;
        }
    }
    Output0[id.xy] = float4(result, 1);
}

bool RecoverConsistentWarp(float2 uv, float2 motion, out float3 result) {
    result = 0;
    float2 pa = uv - Alpha * motion, pb = uv + (1 - Alpha) * motion;
    float2 border = .5 / SourceSize;
    if (any(pa < border) || any(pa > 1 - border) || any(pb < border) || any(pb > 1 - border))
        return false;

    float4 f = FlowAt(Forward, pa), b = FlowAt(Backward, pb);
    float2 forwardError = (f.xy - motion) * SourceSize;
    float2 backwardError = (b.xy + motion) * SourceSize;
    if (min(f.z, b.z) <= .35 || max(dot(forwardError, forwardError), dot(backwardError, backwardError)) > 1.44)
        return false;

    float3 a = A.SampleLevel(LinearClamp, pa, 0).rgb;
    float3 bColor = B.SampleLevel(LinearClamp, pb, 0).rgb;
    if (Max3(abs(a - bColor)) >= .045) return false;
    result = lerp(a, bColor, Alpha);
    return true;
}

bool WarpsAgree(float4 forward, float4 backward) {
    float2 disagreement = (forward.xy + backward.xy) * SourceSize;
    return dot(disagreement, disagreement) <= 2.25;
}

bool MovedAway(Texture2D<float4> reverseField, Texture2D<float4> otherImage,
               float2 uv, float3 color, float4 flow, float phase) {
    float2 displacement = phase * flow.xy * SourceSize;
    if (dot(displacement, displacement) < 2.25) return false;

    float2 match = uv + flow.xy;
    float4 reverse = FlowAt(reverseField, match);
    return ValidWarp(uv, uv, match, flow, reverse, 0)
        && Max3(abs(color - otherImage.SampleLevel(LinearClamp, match, 0).rgb)) < .045;
}

bool RecoverStaticBackground(float2 uv, float3 a, float3 b, out float3 color) {
    bool fromA = Alpha <= .5;
    float4 moving = fromA ? FlowAt(Forward, uv) : FlowAt(Backward, uv);
    float phase = fromA ? Alpha : 1 - Alpha;
    color = 0;
    bool moved = fromA ? MovedAway(Backward, B, uv, a, moving, phase)
                       : MovedAway(Forward, A, uv, b, moving, phase);
    if (!moved) return false;

    // Check the position the moving surface would have come from at this phase.
    // If both frames show the same background there, the motion has exposed this pixel.
    float2 anchor = uv - moving.xy * phase;
    float2 border = .5 / SourceSize;
    if (any(anchor < border) || any(anchor > 1 - border)) return false;

    float3 anchorA = A.SampleLevel(LinearClamp, anchor, 0).rgb;
    float3 anchorB = B.SampleLevel(LinearClamp, anchor, 0).rgb;
    if (Max3(abs(anchorA - anchorB)) > .02) return false;
    float2 f = FlowAt(Forward, anchor).xy * SourceSize;
    float2 r = FlowAt(Backward, anchor).xy * SourceSize;
    if (max(dot(f, f), dot(r, r)) > .25) {
        // Flow can bleed into a static edge. Require a matching image patch
        // along that edge before using the background in this case.
        float2 direction = normalize(moving.xy * SourceSize);
        float2 offset = float2(-direction.y, direction.x) / SourceSize;
        const float tolerance = 2.0 / 255.0;
        if (Max3(abs(anchorA - anchorB)) > tolerance) return false;
        float3 plusA = A.SampleLevel(LinearClamp, anchor + offset, 0).rgb;
        float3 plusB = B.SampleLevel(LinearClamp, anchor + offset, 0).rgb;
        float3 minusA = A.SampleLevel(LinearClamp, anchor - offset, 0).rgb;
        float3 minusB = B.SampleLevel(LinearClamp, anchor - offset, 0).rgb;
        if (max(Max3(abs(plusA - plusB)), Max3(abs(minusA - minusB))) > tolerance) return false;
    }
    color = fromA ? b : a;
    return true;
}

groupshared uint GeneratedCounts[8];
void SynthesizeFrame(uint3 id, uint thread, bool slowMotion) {
    if (thread < 8) GeneratedCounts[thread]=0;
    GroupMemoryBarrierWithGroupSync();
    bool inside=all(id.xy<uint2(SourceSize));
    if(inside) {
        float2 uv=(id.xy+.5)/SourceSize;
        float3 original=A.Load(int3(id.xy,0)).rgb, next=B.Load(int3(id.xy,0)).rgb;
        float difference = Max3(abs(original - next));
        float3 result = Alpha <= .5 ? original : next;
        bool matchedWarp = false;
        if (slowMotion) {
            // Use the nearer real image where the two frames cannot explain the same surface.
            if (difference > .006) {
                float2 pa = uv, pb = uv;
                [unroll] for (int iteration = 0; iteration < 3; iteration++) {
                    pa = uv - Forward.SampleLevel(LinearClamp, pa, 0).xy * Alpha;
                    pb = uv - Backward.SampleLevel(LinearClamp, pb, 0).xy * (1 - Alpha);
                }
                float4 f = Forward.SampleLevel(LinearClamp, pa, 0), b = Backward.SampleLevel(LinearClamp, pb, 0);
                float2 matchB = pa + f.xy, matchA = pb + b.xy;
                float4 opposite = Backward.SampleLevel(LinearClamp, matchB, 0);
                float4 reverse = Forward.SampleLevel(LinearClamp, matchA, 0);
                bool validA = ValidWarp(uv, pa, matchB, f, opposite, Alpha);
                bool validB = ValidWarp(uv, pb, matchA, b, reverse, 1 - Alpha);
                float3 a = A.SampleLevel(LinearClamp, pa, 0).rgb;
                float3 bColor = B.SampleLevel(LinearClamp, pb, 0).rgb;
                // A matching round trip can still cross an object boundary. Check that
                // each warp also projects to a similar color in the opposite endpoint.
                if (validA) validA = Max3(abs(a - B.SampleLevel(LinearClamp, matchB, 0).rgb)) < .085;
                if (validB) validB = Max3(abs(bColor - A.SampleLevel(LinearClamp, matchA, 0).rgb)) < .085;
                if (validA && validB) {
                    if (WarpsAgree(f, b) && Max3(abs(a - bColor)) < .085) {
                        result = lerp(a, bColor, Alpha);
                        matchedWarp = true;
                    }
                } else if (validA) { result = a; matchedWarp = true; }
                else if (validB) { result = bColor; matchedWarp = true; }
                else {
                    // At motion boundaries the interpolated flow can mix two surfaces.
                    // Retry a discrete vector only when both endpoints confirm it.
                    float3 recovered;
                    bool matched = RecoverConsistentWarp(uv, FlowAt(Forward, uv).xy, recovered);
                    if (!matched) {
                        float2 reference = uv + Alpha * FlowAt(Backward, uv).xy;
                        matched = RecoverConsistentWarp(uv, FlowAt(Forward, reference).xy, recovered);
                    }
                    if (matched) { result = recovered; matchedWarp = true; }
                }
            }
        } else if((Flags&2)!=0 && difference > .006) {
            float2 pa=uv,pb=uv;
            [unroll] for(int iteration=0;iteration<3;iteration++) {
                pa=uv-Forward.SampleLevel(LinearClamp,pa,0).xy*Alpha;
                pb=uv-Backward.SampleLevel(LinearClamp,pb,0).xy*(1-Alpha);
            }
            float4 f=Forward.SampleLevel(LinearClamp,pa,0),b=Backward.SampleLevel(LinearClamp,pb,0);
            float2 matchB = pa+f.xy, matchA = pb+b.xy;
            float4 opposite = Backward.SampleLevel(LinearClamp,matchB,0);
            float4 reverse = Forward.SampleLevel(LinearClamp,matchA,0);
            bool validA = ValidWarp(uv,pa,matchB,f,opposite,Alpha);
            bool validB = ValidWarp(uv,pb,matchA,b,reverse,1-Alpha);
            if (max(f.w,b.w)<.2 && validA && validB && WarpsAgree(f, b)) {
                float3 a = A.SampleLevel(LinearClamp,pa,0).rgb, bColor = B.SampleLevel(LinearClamp,pb,0).rgb;
                if (Max3(abs(a-bColor)) < .10) { result = lerp(a,bColor,Alpha); matchedWarp = true; }
            }
            // Try one unblended flow cell at a rejected high-contrast boundary.
            // The existing recovery checks both endpoints before accepting it.
            if (!matchedWarp && difference > .12 && (Flags&4)==0) {
                float2 fromA = FlowAt(Forward,pa).xy, fromB = -FlowAt(Backward,pb).xy;
                bool preferA = f.z >= b.z;
                float2 candidate = preferA ? fromA : fromB;
                float3 recovered;
                bool recoveredWarp = RecoverConsistentWarp(uv,candidate,recovered);
                if (!recoveredWarp && (Flags&8)==0) {
                    float2 disagreement = (fromA - fromB) * SourceSize;
                    if (dot(disagreement,disagreement) > 1)
                        recoveredWarp = RecoverConsistentWarp(uv,preferA ? fromB : fromA,recovered);
                }
                if (recoveredWarp) {
                    result = recovered;
                    matchedWarp = true;
                }
            }
        } else if ((Flags&2)==0) {
            float4 f=FlowAt(Forward,uv);
            float2 pa=uv-f.xy*Alpha;
            float4 refined=FlowAt(Forward,pa);
            if(refined.z>=.45) f=refined;
            pa=uv-f.xy*Alpha; float2 pb=uv+f.xy*(1-Alpha);
            float4 backward=FlowAt(Backward,pb);
            bool safe=f.z>=.45 && backward.z>=.45 && max(f.w,backward.w)<.035
                      &&length((f.xy+backward.xy)*AnalysisSize)<.65
                      &&all(pa>=.5/SourceSize)&&all(pa<=1-.5/SourceSize)
                      &&all(pb>=.5/SourceSize)&&all(pb<=1-.5/SourceSize);
            if(safe && !StableEdge(uv,original,next)) {
                float3 a=A.SampleLevel(LinearClamp,pa,0).rgb,b=B.SampleLevel(LinearClamp,pb,0).rgb;
                if(Max3(abs(a-b))<.05) { result=lerp(a,b,Alpha); matchedWarp = true; }
            }
        }
        if (!matchedWarp && (Flags & 2) != 0 && (Flags & 16) == 0 && difference > .12) {
            float3 background;
            if (RecoverStaticBackground(uv, original, next, background)) {
                result = background;
                matchedWarp = true;
            }
        }
        result=saturate(result);Output0[id.xy]=float4(result,1);
        bool generated = min(Max3(abs(result-original)),Max3(abs(result-next))) > 1.5/255.0;
        // A workgroup has at most 128 pixels, so both counts fit in one add.
        uint counts = (generated ? 1u : 0u) + (!matchedWarp && difference > .14 ? 256u : 0u);
        if (counts != 0) InterlockedAdd(GeneratedCounts[thread & 7], counts);
    }
    GroupMemoryBarrierWithGroupSync();
    if (thread == 0) {
        uint counts = 0;
        [unroll] for (uint bin=0; bin<8; bin++) counts += GeneratedCounts[bin];
        if (counts >= 256) InterlockedAdd(Stats[0], counts >> 8);
        if ((counts & 255) != 0) InterlockedAdd(Stats[6], counts & 255);
    }
}

[numthreads(16,8,1)] void Synthesize(uint3 id:SV_DispatchThreadID,uint thread:SV_GroupIndex) {
    SynthesizeFrame(id, thread, false);
}
[numthreads(16,8,1)] void SynthesizeSlow(uint3 id:SV_DispatchThreadID,uint thread:SV_GroupIndex) {
    SynthesizeFrame(id, thread, true);
}
