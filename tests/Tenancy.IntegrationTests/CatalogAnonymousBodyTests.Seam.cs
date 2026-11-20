using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class CatalogAnonymousBodyTests
{
    // The seam is a test-host-only middleware: a marked request has its body wrapped in a counter and the response
    // reports the bytes the application pulled off it. Nothing in the Api changes; an unmarked request is untouched.
    private const string CountBodyHeader = "X-Test-Count-Body";
    private const string BodyBytesHeader = "X-Test-Body-Bytes";

    private static readonly Action<HttpRequestHeaders> MarkBody =
        headers => headers.Add(CountBodyHeader, "1");

    [Fact]
    public async Task CatalogWrite_AnonymousRequest_ReadsNoBodyBytes()
    {
        await using var world = await StartAsync();
        var target = await TargetAsync(world, "suite-revision");
        var api = TenancyApiFactory.RealResolver(
            fixture.AppConnectionString(world.Database),
            services: services => services.AddSingleton<IStartupFilter>(new CountingBodyStartupFilter()));
        using var client = api.CreateClient();

        try
        {
            var anonymous = await SendBodyAsync(world, target, "{", Anonymous, client: client, extraHeaders: MarkBody);
            anonymous.Answer.Status.ShouldBe(HttpStatusCode.Unauthorized, anonymous.Text);
            BytesRead(anonymous).ShouldBe(0, "an anonymous request is refused before its body is read");

            var authenticated = await SendBodyAsync(world, target, "{", world.Admin, client: client, extraHeaders: MarkBody);
            authenticated.Answer.Status.ShouldBe(HttpStatusCode.BadRequest, authenticated.Text);
            BytesRead(authenticated).ShouldBeGreaterThan(0, "the counter observes the read the positive control makes");
        }
        finally
        {
            await api.DisposeAsync();
        }
    }

    private static long BytesRead(BodyAnswer answer)
    {
        if (!answer.Headers.TryGetValues(BodyBytesHeader, out var values))
        {
            throw new InvalidOperationException(
                $"the counting middleware reported no body-byte header ({answer.Answer.Status} {answer.Text})");
        }

        return long.Parse(values.Single(), CultureInfo.InvariantCulture);
    }

    /// <summary>Mounts the body counter on the test host's pipeline; only a request carrying the marker is wrapped.</summary>
    private sealed class CountingBodyStartupFilter : IStartupFilter
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
