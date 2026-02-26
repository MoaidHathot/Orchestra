using System.Net;
using System.Web;

namespace Orchestra.Mcp.Graph.Authentication;

/// <summary>
/// Result from OAuth callback handling.
/// </summary>
public class OAuthCallbackResult
{
    public string? AuthCode { get; set; }
    public string? Error { get; set; }
    public bool Success => !string.IsNullOrEmpty(AuthCode) && string.IsNullOrEmpty(Error);
}

/// <summary>
/// HTTP listener handler for OAuth redirect callback.
/// </summary>
public class OAuthCallbackHandler : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private bool _disposed;

    public OAuthCallbackHandler(int port)
    {
        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    /// <summary>
    /// Waits for the OAuth callback and extracts the authorization code.
    /// </summary>
    public async Task<OAuthCallbackResult> WaitForCallbackAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var result = new OAuthCallbackResult();

        try
        {
            _listener.Start();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var contextTask = _listener.GetContextAsync();
            var completedTask = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, linkedCts.Token));

            if (completedTask != contextTask)
            {
                result.Error = "OAuth callback timed out";
                return result;
            }

            var context = await contextTask;
            var request = context.Request;
            var response = context.Response;

            // Parse query string
            var query = HttpUtility.ParseQueryString(request.Url?.Query ?? string.Empty);
            result.AuthCode = query["code"];
            result.Error = query["error_description"] ?? query["error"];

            // Send response to browser
            var responseHtml = result.Success
                ? "<html><body><h2>Authentication complete!</h2><p>You can close this window.</p></body></html>"
                : $"<html><body><h2>Authentication failed</h2><p>{HttpUtility.HtmlEncode(result.Error)}</p></body></html>";

            var buffer = System.Text.Encoding.UTF8.GetBytes(responseHtml);
            response.ContentType = "text/html";
            response.ContentLength64 = buffer.Length;
            response.StatusCode = 200;

            await response.OutputStream.WriteAsync(buffer, cancellationToken);
            response.Close();
        }
        catch (OperationCanceledException)
        {
            result.Error = "OAuth callback was cancelled";
        }
        catch (Exception ex)
        {
            result.Error = $"OAuth callback error: {ex.Message}";
        }
        finally
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
                // Ignore errors when stopping
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            try
            {
                _listener.Close();
            }
            catch
            {
                // Ignore errors when closing
            }
        }
    }
}
