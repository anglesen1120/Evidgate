using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Tenancy.IntegrationTests;

internal static class BodyCountSeam
{
    // The seam is a test-host-only middleware: a marked request has its body wrapped in a counter and the response
    // reports the bytes the application pulled off it. Nothing in the Api changes; an unmarked request is untouched.
    internal const string CountBodyHeader = "X-Test-Count-Body";
    internal const string BodyBytesHeader = "X-Test-Body-Bytes";

    internal static readonly Action<HttpRequestHeaders> MarkBody =
        headers => headers.Add(CountBodyHeader, "1");

    internal static long BytesRead(ApiAnswer answer)
    {
        if (!answer.Headers.TryGetValues(BodyBytesHeader, out var values))
        {
            throw new InvalidOperationException(
                $"the counting middleware reported no body-byte header ({answer.Status} {answer.Body.GetRawText()})");
        }

        return long.Parse(values.Single(), CultureInfo.InvariantCulture);
    }

    /// <summary>Mounts the body counter on the test host's pipeline; only a request carrying the marker is wrapped.</summary>
    internal sealed class CountingBodyStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (!context.Request.Headers.ContainsKey(CountBodyHeader))
                {
                    await nextMiddleware().ConfigureAwait(false);
                    return;
                }

                var original = context.Request.Body;
                var counting = new CountingBodyStream(original);
                context.Request.Body = counting;
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[BodyBytesHeader] = counting.BytesRead.ToString(CultureInfo.InvariantCulture);
                    return Task.CompletedTask;
                });

                try
                {
                    await nextMiddleware().ConfigureAwait(false);
                }
                finally
                {
                    context.Request.Body = original;
                }
            });

            next(app);
        };
    }

    /// <summary>Counts every byte a reader pulls off the wrapped request body, sync or async.</summary>
    private sealed class CountingBodyStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;

            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            BytesRead += read;

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
