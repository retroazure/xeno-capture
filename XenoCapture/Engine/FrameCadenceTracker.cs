using System;
using System.Threading;
namespace XenoCapture.Engine;
// Called under the preview host gate, before the latest-frame slot can drop a packet.
// Keeps only 2304 luma samples and seven timing ratios, never a capture-frame backlog.
internal sealed class FrameCadenceTracker
{
    readonly byte[] previous=new byte[64*36];readonly int[] ratios=new int[7];
    int width,height,seen,next,repeatRatio=1;PixelLayout layout;bool initialized;long distinctAt,capturePeriod;
    internal long Period(long capturePeriod)=>capturePeriod*Math.Max(1,Volatile.Read(ref repeatRatio));
    internal void Reset(){initialized=false;seen=next=0;distinctAt=0;Volatile.Write(ref repeatRatio,1);}
    internal void Observe(Packet frame)
    {
        if(frame.Width<=0 || frame.Height<=0 || frame.Duration<=0)return;
        if(frame.Width!=width || frame.Height!=height || frame.Layout!=layout || Math.Abs(frame.Duration-capturePeriod)>Math.Max(1,frame.Duration/100)){Reset();width=frame.Width;height=frame.Height;layout=frame.Layout;capturePeriod=frame.Duration;}
        var bytes=frame.Bytes;int changed=0;
        for(int y=0;y<36;y++) for(int x=0;x<64;x++)
        {
            int sx=(2*x+1)*width/128,sy=(2*y+1)*height/72,index=y*64+x,offset=sy*width+sx;
            byte luma=layout==PixelLayout.Nv12 ? bytes[offset]:(byte)((bytes[offset*4]*19+bytes[offset*4+1]*183+bytes[offset*4+2]*54)>>8);
            if(Math.Abs(luma-previous[index])>3)changed++;previous[index]=luma;
        }
        bool distinct=!initialized || changed>=3;
        if(!distinct)return;
        if(initialized && frame.Time>distinctAt)
        {
            long delta=frame.Time-distinctAt;int ratio=(int)Math.Round(delta/(double)frame.Duration);
            // A pause/menu transition is not evidence that the game changed its frame rate.
            if(ratio is >=1 and <=4 && Math.Abs(delta-ratio*frame.Duration)<frame.Duration/4)
            {
                ratios[next]=ratio;next=(next+1)%ratios.Length;seen=Math.Min(seen+1,ratios.Length);
                int votes=0;for(int i=0;i<seen;i++)if(ratios[i]==ratio)votes++;
                if(votes>=3)Volatile.Write(ref repeatRatio,ratio);
            }
        }
        initialized=true;distinctAt=frame.Time;
    }
}
