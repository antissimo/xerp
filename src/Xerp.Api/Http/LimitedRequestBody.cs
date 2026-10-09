namespace Xerp.Api.Http;

/// <summary>
/// The body of a request that declared no length (chunked): reading past the limit fails with the
/// <see cref="BadHttpRequestException"/> that <see cref="ErrorHandlingMiddleware"/> answers with <c>413</c>.
/// The limit is kept here and not by the server, because a server that cuts a body off also closes the
/// connection, and a client that is still sending then never sees the answer.
/// </summary>
public sealed class LimitedRequestBody(Stream inner, long maxBytes) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

    private int Counted(int read)
    {
        _read += read;
        if (_read > maxBytes)
            throw new BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge);
        return read;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
