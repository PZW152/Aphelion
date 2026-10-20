using Aphelion.Core.Net;

namespace Aphelion.Core.Install;

/// <summary>Progress of a multi-stage operation. <see cref="Stage"/> is a stable key the UI translates.</summary>
public readonly record struct StageProgress(string Stage, double? Fraction = null, string? Detail = null, DownloadProgress? Download = null);

public static class ProgressExtensions
{
    /// <summary>Adapts download progress into stage progress for <paramref name="stage"/>.</summary>
    public static IProgress<DownloadProgress> ForDownloads(this IProgress<StageProgress>? progress, string stage) =>
        new Progress<DownloadProgress>(d => progress?.Report(new StageProgress(stage,
            d.BytesTotal > 0 ? (double)d.BytesDone / d.BytesTotal : d.FilesTotal > 0 ? (double)d.FilesDone / d.FilesTotal : null,
            null, d)));
}

/// <summary>Synchronous IProgress (System.Progress posts to a captured context, which reorders updates).</summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
