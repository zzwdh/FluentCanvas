#nullable enable

namespace FluentCanvas.InkAnalyzerCore;

internal static class InkShapeCandidates
{
    internal static async Task<InkShapeAnalysisResult?> FindAsync(
        int strokeCount,
        Func<int, Task<InkShapeAnalysisResult?>> analyzeSuffixAsync,
        bool preferCircleOrEllipse,
        CancellationToken cancellationToken)
    {
        for (int start = 0; start < strokeCount; start++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fallback = await analyzeSuffixAsync(start);
            if (fallback is null) continue;
            if (!preferCircleOrEllipse) return fallback;

            // A round result on the shortest suffix wins. Longer suffixes were already
            // rejected, and this fallback remains the winner if no shorter one is round.
            for (int shorter = strokeCount - 1; shorter > start; shorter--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await analyzeSuffixAsync(shorter);
                if (result?.Kind is InkShapeKind.Circle or InkShapeKind.Ellipse) return result;
            }

            return fallback;
        }

        return null;
    }
}
