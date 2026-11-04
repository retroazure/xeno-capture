using System;
namespace XenoCapture.Engine;
internal sealed record AiVideoSettings(bool Enabled=false,int OutputHeight=0,int Quality=3)
{
    internal static readonly AiVideoSettings Off=new();
    internal static AiVideoSettings Validate(AiVideoSettings value)=>value.OutputHeight is 0 or 720 or 1080 or 1440 or 2160 && value.Quality is >=1 and <=4 ? value:new(value.Enabled,value.OutputHeight is 0 or 720 or 1080 or 1440 or 2160 ? value.OutputHeight:0,value.Quality is >=1 and <=4 ? value.Quality:3);
    internal string QualityName=>Quality switch{1=>"Performance",2=>"Balanced",4=>"Ultra",_=>"High"};
    internal (int Width,int Height) Target(int sw,int sh,double viewportWidth,double viewportHeight)
    {
        double bw=OutputHeight==0?viewportWidth:OutputHeight*16.0/9,bh=OutputHeight==0?viewportHeight:OutputHeight;
        // Fit the source aspect ratio inside the requested output. Never reduce source detail.
        double factor=Math.Max(1,Math.Min(bw/sw,bh/sh));
        return(Math.Max(sw,(int)Math.Round(sw*factor)),Math.Max(sh,(int)Math.Round(sh*factor)));
    }
}
