#nullable enable

using System;
using System.Collections.Generic;
using System.Windows.Ink;
using System.Windows.Input;

namespace FluentCanvas.Helpers;

internal sealed class InkReplaySession : IDisposable
{
    private readonly IEnumerator<Stroke> strokes;
    private readonly StrokeCollection destination;
    private readonly FrozenInk? snapshot;
    private Stroke? current;

    internal InkReplaySession(IEnumerable<Stroke> source, StrokeCollection destination)
    {
        strokes = source.GetEnumerator();
        this.destination = destination;
    }

    internal InkReplaySession(FrozenInk snapshot, StrokeCollection destination)
        : this(snapshot.ReadStrokes(), destination) => this.snapshot = snapshot;

    public void Dispose()
    {
        strokes.Dispose();
        snapshot?.Dispose();
    }

    private static readonly TimeSpan SamplePause = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan FinalHold = TimeSpan.FromMilliseconds(100);
    private int sampleIndex;
    private int samplesSincePause;
    private TimeSpan nextSampleAt;
    private TimeSpan? finishedAt;
    private Stroke? preview;

    internal bool Advance(TimeSpan elapsed)
    {
        if (finishedAt is TimeSpan end) return elapsed - end >= FinalHold;

        while (current != null || strokes.MoveNext())
        {
            var stroke = current ??= strokes.Current;
            var points = stroke.StylusPoints;
            int start = sampleIndex;
            int pauseThreshold = stroke.ContainsPropertyData(NativeStrokeCodec.ShapeProperty) ? 50 : 1;
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
            current = null;
            sampleIndex = 0;
            preview = null;
        }

        finishedAt = elapsed;
        return false;
    }
}
