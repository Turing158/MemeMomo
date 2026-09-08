using MemeMomo.Services;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace MemeMomo.Editor;

internal sealed record MarkdownImageLoadResult(BitmapSource? Bitmap, string? Error)
{
    public bool IsSuccess => Bitmap is not null;
}

/// <summary>
/// Decodes local assets and HTTPS images off the UI thread and caches both success and failure.
/// WPF's built-in WIC decoder intentionally renders only the first frame of animated formats.
/// </summary>
internal sealed class MarkdownImageLoader : IDisposable
{
    private readonly string _rootDirectory;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, Lazy<Task<MarkdownImageLoadResult>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;
    private int _requestCount;
    private int _successCount;
    private int _failureCount;

    internal MarkdownImageLoader(string rootDirectory, HttpMessageHandler? handler = null)
    {
        _rootDirectory = rootDirectory;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    internal int RequestCount => Volatile.Read(ref _requestCount);
    internal int SuccessCount => Volatile.Read(ref _successCount);
    internal int FailureCount => Volatile.Read(ref _failureCount);
    internal int CachedResourceCount => _cache.Count;

    /// <summary>
    /// Returns the decoded bitmap when a previous <see cref="LoadAsync"/> for the same
    /// source has already finished successfully. Lets inline element construction build
    /// the final-size image control synchronously instead of growing a placeholder after
    /// it entered the visual tree, which would make AvalonEdit rebuild the visual line
    /// again. Never starts a load and never counts as a request.
    /// </summary>
    internal bool TryGetCompleted(string? source, out MarkdownImageLoadResult? result)
    {
        result = null;
        if (Volatile.Read(ref _disposed) != 0 ||
            !TryNormalizeSource(source, out string normalized, out _))
        {
            return false;
        }
        if (_cache.TryGetValue(normalized, out Lazy<Task<MarkdownImageLoadResult>>? lazy) &&
            lazy.IsValueCreated &&
            lazy.Value.IsCompletedSuccessfully)
        {
            result = lazy.Value.Result;
            return result.IsSuccess;
        }
        return false;
    }

    internal Task<MarkdownImageLoadResult> LoadAsync(string? source, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Task.FromResult(new MarkdownImageLoadResult(null, LocalizationService.Get("图片加载器已释放")));
        }

        if (!TryNormalizeSource(source, out string normalized, out string? error))
        {
            Interlocked.Increment(ref _requestCount);
            Interlocked.Increment(ref _failureCount);
            return Task.FromResult(new MarkdownImageLoadResult(null, error));
        }

        Lazy<Task<MarkdownImageLoadResult>> lazy = _cache.GetOrAdd(
            normalized,
            key => new Lazy<Task<MarkdownImageLoadResult>>(
                () => LoadCoreAsync(key, _lifetime.Token),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Interlocked.Increment(ref _requestCount);
        return AwaitWithCancellationAsync(lazy.Value, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        _httpClient.Dispose();
    }

    private async Task<MarkdownImageLoadResult> LoadCoreAsync(string source, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes;
            if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                bytes = await _httpClient.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
            }

            if (bytes.LongLength > Services.MarkdownImageStore.MaximumImageBytes)
            {
                throw new InvalidDataException(LocalizationService.Get("图片不能超过 20 MB。"));
            }

            BitmapSource bitmap = await Task.Run(() => Decode(bytes), cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _successCount);
            return new MarkdownImageLoadResult(bitmap, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _failureCount);
            return new MarkdownImageLoadResult(null, exception.Message);
        }
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private bool TryNormalizeSource(string? source, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            error = LocalizationService.Get("图片地址为空");
            return false;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                error = LocalizationService.Get("仅支持 HTTPS 图片地址");
                return false;
            }
            normalized = uri.AbsoluteUri;
            return true;
        }

        string relative = source.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(_rootDirectory, relative));
        string assetRoot = Path.GetFullPath(_rootDirectory);
        if (!full.StartsWith(assetRoot, StringComparison.OrdinalIgnoreCase))
        {
            error = LocalizationService.Get("图片路径无效");
            return false;
        }
        normalized = full;
        return true;
    }

    private static async Task<MarkdownImageLoadResult> AwaitWithCancellationAsync(
        Task<MarkdownImageLoadResult> task,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await task;
        }

        Task completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return await (Task<MarkdownImageLoadResult>)completed;
    }
}
