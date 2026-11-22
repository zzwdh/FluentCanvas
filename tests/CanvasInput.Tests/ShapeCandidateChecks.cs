#nullable enable

using FluentCanvas.InkAnalyzerCore;

internal static class ShapeCandidateChecks
{
    internal static async Task RunAsync()
    {
        InkShapeKind?[] kinds = [null, InkShapeKind.Circle, InkShapeKind.Ellipse, InkShapeKind.Triangle];
        int patterns = 1;
        for (int strokeCount = 0; strokeCount <= 4; strokeCount++, patterns *= kinds.Length)
        {
            for (int pattern = 0; pattern < patterns; pattern++)
            {
                var results = new InkShapeAnalysisResult?[strokeCount];
                int remaining = pattern;
                for (int start = 0; start < strokeCount; start++)
                {
                    if (kinds[remaining % kinds.Length] is InkShapeKind kind)
                        results[start] = new InkShapeAnalysisResult(kind, default, default, [], [start]);
                    remaining /= kinds.Length;
                }

                await VerifySelectionAsync(results, preferRound: false);
                await VerifySelectionAsync(results, preferRound: true);
            }
        }

        using var cancellation = new CancellationTokenSource();
        try
        {
            await InkShapeCandidates.FindAsync(2, start =>
            {
                if (start != 0) throw new InvalidOperationException("A canceled recognition should not analyze another suffix.");
                cancellation.Cancel();
                return Task.FromResult<InkShapeAnalysisResult?>(new(InkShapeKind.Triangle, default, default, [], [start]));
            }, preferCircleOrEllipse: true, cancellation.Token);
            throw new InvalidOperationException("Cancellation should interrupt circular candidate selection.");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token) { }
        Console.WriteLine("PASS Shape candidate order matches the previous policy for all 341 patterns and avoids duplicate analysis");
    }

    private static async Task VerifySelectionAsync(InkShapeAnalysisResult?[] results, bool preferRound)
    {
        var expected = results.FirstOrDefault(result => result != null);
        if (preferRound)
        {
            // The old code searched again from every starting index, including previous misses.
            for (int start = results.Length - 1; start >= 0; start--)
            {
                var candidate = results.Skip(start).FirstOrDefault(result => result != null);
                if (candidate?.Kind is not (InkShapeKind.Circle or InkShapeKind.Ellipse)) continue;
                expected = candidate;
                break;
            }
        }

        var tested = new HashSet<int>();
        var actual = await InkShapeCandidates.FindAsync(results.Length, start =>
        {
            if (!tested.Add(start)) throw new InvalidOperationException("A suffix should be analyzed once per request.");
            return Task.FromResult(results[start]);
        }, preferRound, CancellationToken.None);
        if (!ReferenceEquals(actual, expected))
            throw new InvalidOperationException("Recognition should keep the same fallback and round-shape precedence.");
    }
}
