namespace SwitchEmuModDownloader.Classes.Extensions;

// https://stackoverflow.com/a/46497896
public static class HttpClientExtensions
{
    /// <summary>
    /// Downloads <paramref name="requestUri"/> into <paramref name="destination"/>.
    /// </summary>
    /// <returns>
    /// <c>null</c> on success; otherwise a short description of what went wrong
    /// (HTTP status, timeout, network error). Never throws.
    /// </returns>
    public static async Task<string?> DownloadAsync(this HttpClient client, string requestUri, Stream destination, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // generous timeout for the headers: GitHub raw / large archives can be slow to start
            using var requestCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Get the http headers first to examine the content length
            using var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, requestCancellationTokenSource.Token);
            if (!response.IsSuccessStatusCode)
                return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            var contentLength = response.Content.Headers.ContentLength;
            await using var download = await response.Content.ReadAsStreamAsync(cancellationToken);

            // Ignore progress reporting when no progress reporter was
            // passed or when the content length is unknown
            if (progress is null || !contentLength.HasValue)
            {
                await download.CopyToAsync(destination, cancellationToken);
                return null;
            }

            // Convert absolute progress (bytes downloaded) into relative progress (0% - 100%)
            var relativeProgress = new Progress<long>(totalBytes => progress.Report((float)totalBytes / contentLength.Value));
            // Use extension method to report progress while downloading
            await download.CopyToAsync(destination, 8192, relativeProgress, cancellationToken);
            progress.Report(1);
            return null;
        }
        catch (OperationCanceledException)
        {
            return "timeout";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
