#nullable enable

using System;
using System.Windows.Ink;
using System.Windows.Input;

namespace FluentCanvas.Helpers;

internal sealed class InkReplaySession(StrokeCollection source, StrokeCollection destination)
{
    private static readonly TimeSpan SamplePause = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan FinalHold = TimeSpan.FromMilliseconds(100);
    private int strokeIndex;
    private int sampleIndex;
    private int samplesSincePause;
    private TimeSpan nextSampleAt;
    private TimeSpan? finishedAt;
    private Stroke? preview;

    internal bool Advance(TimeSpan elapsed)
    {
        if (finishedAt is TimeSpan end) return elapsed - end >= FinalHold;

        while (strokeIndex < source.Count)
        {
            var stroke = source[strokeIndex];
            var points = stroke.StylusPoints;
            int start = sampleIndex;
            // The geometry tool generates circles and ellipses with 629 samples. They have
            // always replayed in larger groups than handwritten strokes.
            int pauseThreshold = points.Count == 629 ? 50 : 1;
            while (sampleIndex < points.Count)
            {
                bool shouldPause = samplesSincePause >= pauseThreshold;
                TimeSpan sampleAt = shouldPause ? nextSampleAt + SamplePause : nextSampleAt;
                if (sampleAt > elapsed) break;
                nextSampleAt = sampleAt;
                samplesSincePause = shouldPause ? 0 : samplesSincePause + 1;
                sampleIndex++;
            }

            int count = sampleIndex - start;
            if (count > 0)
            {
                var batch = new StylusPointCollection(points.Description, count);
                for (int i = start; i < sampleIndex; i++) batch.Add(points[i]);
                if (preview is null)
                {
                    preview = new Stroke(batch, stroke.DrawingAttributes.Clone());
                    destination.Add(preview);
                }
                else
                {
                    preview.StylusPoints.Add(batch);
                }
            }

            if (sampleIndex < points.Count) return false;
            strokeIndex++;
            sampleIndex = 0;
            preview = null;
        }

        finishedAt = elapsed;
        return false;
    }
}
