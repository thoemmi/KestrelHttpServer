// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Testing;
using Xunit;

namespace Microsoft.AspNetCore.Server.Kestrel.FunctionalTests
{
    public class ConnectionTimeoutTests
    {
        private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan LongDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ShortDelay = TimeSpan.FromSeconds(LongDelay.TotalSeconds / 10);

        [Fact]
        public async Task TestConnectionTimeout()
        {
            using (var server = CreateServer())
            {
                var tasks = new[]
                {
                    ConnectionClosedWhenTimeoutExpires(server),
                    ConnectionKeptAliveBetweenRequests(server),
                    ConnectionNotTimedOutWhileRequestBeginSent(server),
                    ConnectionTimesOutWhenOpenedButNoRequestSent(server)
                };

                await Task.WhenAll(tasks);
            }
        }

        [Fact]
        public async Task ConnectionNotTimedOutWhileResponseBeingSent()
        {
            var cts = new CancellationTokenSource();
            var sem = new SemaphoreSlim(0);
            var chunks = 0;

            using (var server = CreateServer(async httpContext =>
            {
                if (httpContext.Request.Path == "/longresponse")
                {
                    while (!cts.IsCancellationRequested)
                    {
                        await httpContext.Response.WriteAsync("a");
                        Interlocked.Increment(ref chunks);
                        sem.Release();
                    }

                    sem.Release();
                }
                else
                {
                    const string response = "hello, world";
                    httpContext.Response.ContentLength = response.Length;
                    await httpContext.Response.WriteAsync(response);
                }
            }))
            {
                using (var connection = new TestConnection(server.Port))
                {
                    await connection.Send(
                        "GET /longresponse HTTP/1.1",
                        "",
                        "");
                    cts.CancelAfter(LongDelay);
                    await connection.Receive(
                        "HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Transfer-Encoding: chunked",
                        "",
                        "");

                    while (true)
                    {
                        await sem.WaitAsync();

                        if (chunks == 0)
                        {
                            break;
                        }

                        await connection.Receive(
                            "1",
                            "a",
                            "");
                        Interlocked.Decrement(ref chunks);
                    }

                    await connection.Receive(
                            "0",
                            "",
                            "");

                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await ReceiveResponse(connection, server.Context);
                }
            }
        }

        [Fact]
        public async Task ConnectionTimeoutDoesNotApplyToUpgradedConnections()
        {
            var cts = new CancellationTokenSource();

            using (var server = CreateServer(async httpContext =>
            {
                if (httpContext.Request.Path == "/upgrade")
                {
                    using (var stream = await httpContext.Features.Get<IHttpUpgradeFeature>().UpgradeAsync())
                    {
                        cts.Token.WaitHandle.WaitOne();
                        stream.Write(new byte[] { (byte)'a' }, 0, 1);
                    }
                }
                else
                {
                    const string response = "hello, world";
                    httpContext.Response.ContentLength = response.Length;
                    await httpContext.Response.WriteAsync(response);
                }
            }))
            {
                using (var connection = new TestConnection(server.Port))
                {
                    await connection.Send(
                        "GET /upgrade HTTP/1.1",
                        "",
                        "");
                    await connection.Receive(
                        "HTTP/1.1 101 Switching Protocols",
                        "Connection: Upgrade",
                        $"Date: {server.Context.DateHeaderValue}",
                        "",
                        "");

                    cts.CancelAfter(LongDelay);
                    await connection.Receive("a");
                }
            }
        }

        private async Task ConnectionClosedWhenTimeoutExpires(TestServer server)
        {
            using (var connection = new TestConnection(server.Port))
            {
                await connection.Send(
                    "GET / HTTP/1.1",
                    "",
                    "");
                await ReceiveResponse(connection, server.Context);

                await Task.Delay(LongDelay);

                await Assert.ThrowsAsync<IOException>(async () =>
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await ReceiveResponse(connection, server.Context);
                });
            }
        }

        private async Task ConnectionKeptAliveBetweenRequests(TestServer server)
        {
            using (var connection = new TestConnection(server.Port))
            {
                for (var i = 0; i < 10; i++)
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await Task.Delay(ShortDelay);
                }

                for (var i = 0; i < 10; i++)
                {
                    await ReceiveResponse(connection, server.Context);
                }
            }
        }

        private async Task ConnectionNotTimedOutWhileRequestBeginSent(TestServer server)
        {
            using (var connection = new TestConnection(server.Port))
            {
                var cts = new CancellationTokenSource();
                cts.CancelAfter(LongDelay);

                await connection.Send(
                        "POST / HTTP/1.1",
                        "Transfer-Encoding: chunked",
                        "",
                        "");

                while (!cts.IsCancellationRequested)
                {

                    await connection.Send(
                        "1",
                        "a",
                        "");
                }

                await connection.Send(
                        "0",
                        "",
                        "");

                await ReceiveResponse(connection, server.Context);
            }
        }

        private async Task ConnectionTimesOutWhenOpenedButNoRequestSent(TestServer server)
        {
            using (var connection = new TestConnection(server.Port))
            {
                await Task.Delay(LongDelay);
                await Assert.ThrowsAsync<IOException>(async () =>
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                });
            }
        }

        private TestServer CreateServer(RequestDelegate app = null)
        {
            return new TestServer(app ?? App, new TestServiceContext
            {
                ServerOptions = new KestrelServerOptions
                {
                    AddServerHeader = false,
                    Limits =
                    {
                        ConnectionTimeout = KeepAliveTimeout
                    }
                }
            });
        }

        private async Task App(HttpContext httpContext)
        {
            const string response = "hello, world";
            httpContext.Response.ContentLength = response.Length;
            await httpContext.Response.WriteAsync(response);
        }

        private async Task ReceiveResponse(TestConnection connection, TestServiceContext testServiceContext)
        {
            await connection.Receive(
                "HTTP/1.1 200 OK",
                $"Date: {testServiceContext.DateHeaderValue}",
                "Content-Length: 12",
                "",
                "hello, world");
        }
    }
}
