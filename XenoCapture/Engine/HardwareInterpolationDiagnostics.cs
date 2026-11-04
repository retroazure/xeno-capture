using System;
using System.Collections.Generic;
using System.IO;
namespace XenoCapture.Engine;
internal static class HardwareInterpolationDiagnostics
{
    static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    internal static Dictionary<string,object> Run(string folder)
    {
        var checks=new Dictionary<string,object>();
        foreach(bool localized in new[]{false,true})
        {
            using var first=Scene(0,localized);using var last=Scene(1,localized);using var expected=Scene(.5,localized);
            first.Time=1_000_000;last.Time=1_333_333;
            using var renderer=new D3DPreview(0,640,360){DisableFrameBudgetForDiagnostics=true};
            Require(renderer.PrepareInterpolation(first)&&renderer.PrepareInterpolation(last),"Motion fixture failed to prepare.");
            Require(renderer.CanGenerateMidpoint,"Camera/object motion was rejected: "+renderer.FrameGenerationDetail);
            var info=renderer.RenderMidpoint(0,PreviewUpscaler.Standard);var pixels=renderer.Readback();
            Require(info.Kind==PresentationKind.Interpolated && renderer.GeneratedPixelFraction>.001,"The motion fixture only repeated source pixels.");
            double generated=0,repeated=0,blend=0;long samples=0;
            for(int y=80;y<320;y++)for(int x=40;x<600;x++)
            {
                // The localized oracle measures the object and its reveal boundary.
                if(localized && (x<200||x>330||y<120||y>250))continue;
                for(int c=0;c<3;c++){int i=(y*640+x)*4+c;generated+=Math.Abs(pixels[i]-expected.Bytes[i]);repeated+=Math.Abs(first.Bytes[i]-expected.Bytes[i]);blend+=Math.Abs((first.Bytes[i]+last.Bytes[i])*.5-expected.Bytes[i]);samples++;}
            }
            generated/=samples;repeated/=samples;blend/=samples;
            string name=localized?"localizedObject":"cameraRotationZoomAndObject";
            checks[name]=new {hardware=renderer.HardwareOpticalFlowActive,generatedError=generated,repeatedError=repeated,blendedError=blend,generatedPixelFraction=renderer.GeneratedPixelFraction,analysisMs=renderer.FrameAnalysisMs};
            File.WriteAllText(Path.Combine(folder,"motion-quality-stage.json"),System.Text.Json.JsonSerializer.Serialize(checks));
            Require(generated<repeated*.85,"Interpolation did not improve moving-object/camera accuracy.");
            Require(generated<blend*.95,"Interpolation did not improve on a simple frame blend.");
            UpscalingDiagnostics.SavePng(Path.Combine(folder,name+"-generated.png"),pixels,640,360);
            UpscalingDiagnostics.SavePng(Path.Combine(folder,name+"-expected.png"),expected.Bytes,640,360);
        }
        checks["fastBoundaryRecovery"] = CheckFastBoundaryRecovery();
        using(var software=new D3DPreview(0,640,360){DisableFrameBudgetForDiagnostics=true,ForceSoftwareFlowForDiagnostics=true})
        using(var left=FrameGenerationDiagnostics.Pattern(0))using(var right=FrameGenerationDiagnostics.Pattern(16))
        {
            left.Time=1_000_000;right.Time=1_333_333;
            Require(software.PrepareInterpolation(left)&&software.PrepareInterpolation(right)&&software.CanGenerateMidpoint&&!software.HardwareOpticalFlowActive,"Software motion fallback failed.");
            Require(software.RenderMidpoint(0,PreviewUpscaler.Standard).Kind==PresentationKind.Interpolated,"Software fallback only repeated source frames.");
            software.ResetInterpolation();Require(software.FrameGenerationSurfaceBytes==0,"Software fallback retained history after reset.");
            checks["softwareFallbackAndRelease"]="pass";
        }
        var tracker=new FrameCadenceTracker();using var a=FrameGenerationDiagnostics.Pattern(0);using var b=FrameGenerationDiagnostics.Pattern(12);
        for(int i=0;i<24;i++){var p=(i/2)%2==0?a:b;p.Duration=166667;p.Time=1_000_000+i*166667L;tracker.Observe(p);}
        Require(tracker.Period(166667)==333334,"30 FPS content inside 60 FPS input was not detected.");
        for(int i=24;i<36;i++){var p=i%2==0?a:b;p.Time=1_000_000+i*166667L;tracker.Observe(p);}
        Require(tracker.Period(166667)==166667,"Automatic content cadence did not return to 60 FPS.");
        tracker.Reset();Require(tracker.Period(166667)==166667,"Cadence reset retained the previous session.");
        checks["automatic30Then60CadenceAndReset"]="pass";return checks;
    }
    static object CheckFastBoundaryRecovery()
    {
        using var renderer = new D3DPreview(0, 640, 360) { DisableFrameBudgetForDiagnostics = true };
        var cases = new Dictionary<string, object>();
        foreach (bool thin in new[] { false, true })
        {
            renderer.ResetInterpolation();
            using var previous = Scene(0, localized: true, thin);
            using var current = Scene(1, localized: true, thin);
            previous.Time = 1_000_000;
            current.Time = previous.Time + 166667;
            previous.Duration = current.Duration = 166667;
            Require(renderer.PrepareInterpolation(previous) && renderer.PrepareInterpolation(current), "Fast boundary fixture failed to prepare.");
            if (!renderer.HardwareOpticalFlowActive) return "skipped: NVIDIA optical flow unavailable";
            Require(renderer.CanGenerateMidpoint, "Fast boundary fixture rejected all motion.");
            long before = 0, primaryOnly = 0, after = 0;
            int changed = 0, alternateChanged = 0;
            for (int step = 1; step <= 3; step++)
            {
                using var expected = Scene(step / 4.0, localized: true, thin);
                renderer.DisableFastBoundaryRecoveryForDiagnostics = true;
                renderer.RenderIntermediate(step, 4, 0, PreviewUpscaler.Standard);
                byte[] baseline = renderer.Readback();
                renderer.DisableFastBoundaryRecoveryForDiagnostics = false;
                renderer.DisableAlternateWarpForDiagnostics = true;
                renderer.RenderIntermediate(step, 4, 0, PreviewUpscaler.Standard);
                byte[] primary = renderer.Readback();
                renderer.DisableAlternateWarpForDiagnostics = false;
                renderer.RenderIntermediate(step, 4, 0, PreviewUpscaler.Standard);
                byte[] recovered = renderer.Readback();
                for (int y = 125; y < 245; y++)
                    for (int x = 205; x < 340; x++)
                        for (int c = 0; c < 3; c++)
                        {
                            int i = (y * 640 + x) * 4 + c;
                            before += Math.Abs(baseline[i] - expected.Bytes[i]);
                            primaryOnly += Math.Abs(primary[i] - expected.Bytes[i]);
                            after += Math.Abs(recovered[i] - expected.Bytes[i]);
                            if (c == 0 && baseline[i] != recovered[i]) changed++;
                            if (c == 0 && primary[i] != recovered[i]) alternateChanged++;
                        }
            }
            Require(after <= before * 1.02, "Boundary recovery made the fast motion fixture less accurate.");
            // Leave room for drivers that already estimate this fixture accurately.
            if (!thin && before > 380_000)
                Require(changed > 0 && after < before * .95, "The fast path did not recover the moving edge.");
            Require(after <= primaryOnly * 1.02, "The second verified candidate made the fixture less accurate.");
            cases[thin ? "thin" : "wide"] = new { baselineError = before, singleCandidateError = primaryOnly, recoveredError = after, changed, alternateChanged };
        }
        return cases;
    }

    static uint Hash(int x,int y){uint v=unchecked((uint)(x*73856093^y*19349663));v^=v>>13;v*=1274126177;return v^(v>>16);}
    static double Texture(double x,double y,int c)
    {
        int ix=(int)Math.Floor(x/10),iy=(int)Math.Floor(y/10);double fx=x/10-ix,fy=y/10-iy;fx=fx*fx*(3-2*fx);fy=fy*fy*(3-2*fy);
        double V(int a,int b)=>(Hash(a,b)>>(c*8)&255)/255.0;
        return (.15+.65*((1-fy)*((1-fx)*V(ix,iy)+fx*V(ix+1,iy))+fy*((1-fx)*V(ix,iy+1)+fx*V(ix+1,iy+1))));
    }
    static Packet Scene(double t,bool localized,bool thin = false)
    {
        var p=new Packet(640*360*4,false,640,360){Duration=333333};double angle=localized?0:t*.025,scale=localized?1:1+t*.025,move=localized?0:t*12;
        for(int y=0;y<360;y++)for(int x=0;x<640;x++)
        {
            double dx=(x-320-move)/scale,dy=(y-180-(localized?0:t*4))/scale;
            double wx=dx*Math.Cos(angle)+dy*Math.Sin(angle)+320,wy=-dx*Math.Sin(angle)+dy*Math.Cos(angle)+180;
            bool foreground=x>=220+t*12 && x<(thin ? 228 : 300)+t*12 && y>=140+t*8 && y<220+t*8;
            int index=(y*640+x)*4;
            for(int c=0;c<3;c++)p.Bytes[index+c]=(byte)Math.Clamp((int)Math.Round(255*(foreground?Texture(x-t*12+103,y-t*8+49,c):Texture(wx,wy,c))),0,255);
            p.Bytes[index+3]=255;
            if(x>=40&&x<200&&y>=30&&y<68){byte hud=(byte)(x%12<3&&y>36&&y<60?240:16);p.Bytes[index]=p.Bytes[index+1]=p.Bytes[index+2]=hud;}
        }
        return p;
    }
}
