using System;
using System.Threading;
namespace XenoCapture.Engine;
// Called under the preview host gate, before the latest-frame slot can drop a packet.
// Keeps only 2304 luma samples and seven timing ratios, never a capture-frame backlog.
internal sealed class FrameCadenceTracker
{
    const int Columns = 64, Rows = 36;
    readonly byte[] previous = new byte[Columns * Rows];
    readonly int[] sampleColumns = new int[Columns], sampleRows = new int[Rows];
    readonly int[] ratios = new int[7];
    int width,height,seen,next,repeatRatio=1;PixelLayout layout;bool initialized;long distinctAt,capturePeriod;
    internal long Period(long capturePeriod)=>capturePeriod*Math.Max(1,Volatile.Read(ref repeatRatio));
    internal void Reset(){initialized=false;seen=next=0;distinctAt=0;Volatile.Write(ref repeatRatio,1);}
    internal void Observe(Packet frame)
    {
        if(frame.Width<=0 || frame.Height<=0 || frame.Duration<=0)return;
        bool formatChanged = frame.Width != width || frame.Height != height || frame.Layout != layout;
        if (formatChanged || Math.Abs(frame.Duration - capturePeriod) > Math.Max(1, frame.Duration / 100))
        {
            Reset();
            capturePeriod = frame.Duration;
            if (formatChanged) SetSampleOffsets(frame.Width, frame.Height, frame.Layout);
        }

        var bytes = frame.Bytes;
        bool nv12 = layout == PixelLayout.Nv12;
        int changed = 0, sample = 0;
        foreach (int row in sampleRows)
            foreach (int column in sampleColumns)
            {
                int offset = row + column;
                byte luma = nv12 ? bytes[offset]
                    : (byte)((bytes[offset] * 19 + bytes[offset + 1] * 183 + bytes[offset + 2] * 54) >> 8);
                if (Math.Abs(luma - previous[sample]) > 3) changed++;
                previous[sample++] = luma;
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

    void SetSampleOffsets(int frameWidth, int frameHeight, PixelLayout pixelLayout)
    {
        width = frameWidth;
        height = frameHeight;
        layout = pixelLayout;
        int pixelBytes = layout == PixelLayout.Nv12 ? 1 : 4;
        for (int x = 0; x < Columns; x++)
            sampleColumns[x] = ((2 * x + 1) * width / (2 * Columns)) * pixelBytes;
        for (int y = 0; y < Rows; y++)
            sampleRows[y] = ((2 * y + 1) * height / (2 * Rows)) * width * pixelBytes;
    }
}
