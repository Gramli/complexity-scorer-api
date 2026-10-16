namespace ComplexityScorer.Infrastructure;

internal static class BoundedBody
{
    // Enforce the limit even when Content-Length is missing (chunked requests/responses).
    public static async Task<byte[]> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(maximumBytes + 1, 8192)];
        while (true)
        {
            var count = await stream.ReadAsync(
                chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximumBytes + 1 - buffer.Length)),
                cancellationToken);
            if (count == 0)
                return buffer.ToArray();
            if (buffer.Length + count > maximumBytes)
                throw new BodyTooLargeException();
            buffer.Write(chunk, 0, count);
        }
    }
}

internal sealed class BodyTooLargeException : Exception;
