using System.Diagnostics;
using System.Text;

namespace KnowledgeBase.Core;

public sealed record ExtractorProcessResult(byte[] StandardOutput, byte[] StandardError, int ExitCode);

public static class ExtractorProcessRunner
{
    public static async Task<ExtractorProcessResult> RunAsync(ProcessStartInfo start, string request, TimeSpan timeout,
        CancellationToken cancellationToken = default, int maxStdoutBytes = 4 * 1024 * 1024, int maxStderrBytes = 64 * 1024)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        start.UseShellExecute = false; start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start local extractor");
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);
        var output = ReadBoundedAsync(process.StandardOutput.BaseStream, maxStdoutBytes, linked.Token);
        var error = ReadBoundedAsync(process.StandardError.BaseStream, maxStderrBytes, linked.Token);
        var input = WriteAsync(process, request, linked.Token);
        var exit = process.WaitForExitAsync(linked.Token);
        try
        {
            await Task.WhenAll(input, output, error, exit).ConfigureAwait(false);
            return new(output.Result, error.Result, process.ExitCode);
        }
        catch (Exception ex)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(input, output, error, process.WaitForExitAsync()).ConfigureAwait(false); } catch { }
            if (timeoutSource.IsCancellationRequested) throw new TimeoutException($"Local extractor exceeded the {timeout.TotalSeconds:0} second limit", ex);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("Local extractor was cancelled", ex, cancellationToken);
            throw;
        }
    }

    private static async Task WriteAsync(Process process, string request, CancellationToken token)
    {
        await process.StandardInput.WriteAsync(request.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maxBytes) throw new InvalidDataException($"Local extractor stream exceeds {maxBytes} bytes");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
