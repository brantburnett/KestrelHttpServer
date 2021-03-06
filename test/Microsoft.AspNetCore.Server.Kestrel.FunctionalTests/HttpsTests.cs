﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions.Internal;
using Xunit;

namespace Microsoft.AspNetCore.Server.Kestrel.FunctionalTests
{
    public class HttpsTests
    {
        [Fact]
        public async Task EmptyRequestLoggedAsInformation()
        {
            var loggerFactory = new HandshakeErrorLoggerFactory();

            var hostBuilder = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.UseHttps(@"TestResources/testCert.pfx", "testPassword");
                })
                .UseUrls("https://127.0.0.1:0/")
                .UseLoggerFactory(loggerFactory)
                .Configure(app => { });

            using (var host = hostBuilder.Build())
            {
                host.Start();

                using (await HttpClientSlim.GetSocket(new Uri($"http://127.0.0.1:{host.GetPort()}/")))
                {
                    // Close socket immediately
                }

                await loggerFactory.FilterLogger.LogTcs.Task.TimeoutAfter(TimeSpan.FromSeconds(10));
            }

            Assert.Equal(1, loggerFactory.FilterLogger.LastEventId.Id);
            Assert.Equal(LogLevel.Information, loggerFactory.FilterLogger.LastLogLevel);
            Assert.Equal(0, loggerFactory.ErrorLogger.TotalErrorsLogged);
        }

        [Fact]
        public async Task ClientHandshakeFailureLoggedAsInformation()
        {
            var loggerFactory = new HandshakeErrorLoggerFactory();

            var hostBuilder = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.UseHttps(@"TestResources/testCert.pfx", "testPassword");
                })
                .UseUrls("https://127.0.0.1:0/")
                .UseLoggerFactory(loggerFactory)
                .Configure(app => { });

            using (var host = hostBuilder.Build())
            {
                host.Start();

                using (var socket = await HttpClientSlim.GetSocket(new Uri($"https://127.0.0.1:{host.GetPort()}/")))
                using (var stream = new NetworkStream(socket))
                {
                    // Send null bytes and close socket
                    await stream.WriteAsync(new byte[10], 0, 10);
                }

                await loggerFactory.FilterLogger.LogTcs.Task.TimeoutAfter(TimeSpan.FromSeconds(10));
            }

            Assert.Equal(1, loggerFactory.FilterLogger.LastEventId.Id);
            Assert.Equal(LogLevel.Information, loggerFactory.FilterLogger.LastLogLevel);
            Assert.Equal(0, loggerFactory.ErrorLogger.TotalErrorsLogged);
        }

        // Regression test for https://github.com/aspnet/KestrelHttpServer/issues/1103#issuecomment-246971172
        [Fact]
        public async Task DoesNotThrowObjectDisposedExceptionOnConnectionAbort()
        {
            var x509Certificate2 = new X509Certificate2(@"TestResources/testCert.pfx", "testPassword");
            var loggerFactory = new HandshakeErrorLoggerFactory();
            var hostBuilder = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.UseHttps(@"TestResources/testCert.pfx", "testPassword");
                })
                .UseUrls("https://127.0.0.1:0/")
                .UseLoggerFactory(loggerFactory)
                .Configure(app => app.Run(async httpContext =>
                {
                    var ct = httpContext.RequestAborted;
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            await httpContext.Response.WriteAsync($"hello, world", ct);
                            await Task.Delay(1000, ct);
                        }
                        catch (TaskCanceledException)
                        {
                            // Don't regard connection abort as an error
                        }
                    }
                }));

            using (var host = hostBuilder.Build())
            {
                host.Start();

                using (var socket = await HttpClientSlim.GetSocket(new Uri($"https://127.0.0.1:{host.GetPort()}/")))
                using (var stream = new NetworkStream(socket, ownsSocket: false))
                using (var sslStream = new SslStream(stream, true, (sender, certificate, chain, errors) => true))
                {
                    await sslStream.AuthenticateAsClientAsync("127.0.0.1", clientCertificates: null,
                        enabledSslProtocols: SslProtocols.Tls11 | SslProtocols.Tls12,
                        checkCertificateRevocation: false);

                    var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n");
                    await sslStream.WriteAsync(request, 0, request.Length);
                    await sslStream.ReadAsync(new byte[32], 0, 32);
                }
            }

            Assert.False(loggerFactory.ErrorLogger.ObjectDisposedExceptionLogged);
        }

        [Fact]
        public async Task DoesNotThrowObjectDisposedExceptionFromWriteAsyncAfterConnectionIsAborted()
        {
            var tcs = new TaskCompletionSource<object>();
            var x509Certificate2 = new X509Certificate2(@"TestResources/testCert.pfx", "testPassword");
            var loggerFactory = new HandshakeErrorLoggerFactory();
            var hostBuilder = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.UseHttps(@"TestResources/testCert.pfx", "testPassword");
                })
                .UseUrls("https://127.0.0.1:0/")
                .UseLoggerFactory(loggerFactory)
                .Configure(app => app.Run(async httpContext =>
                {
                    httpContext.Abort();
                    try
                    {
                        await httpContext.Response.WriteAsync($"hello, world");
                        tcs.SetResult(null);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                }));

            using (var host = hostBuilder.Build())
            {
                host.Start();

                using (var socket = await HttpClientSlim.GetSocket(new Uri($"https://127.0.0.1:{host.GetPort()}/")))
                using (var stream = new NetworkStream(socket, ownsSocket: false))
                using (var sslStream = new SslStream(stream, true, (sender, certificate, chain, errors) => true))
                {
                    await sslStream.AuthenticateAsClientAsync("127.0.0.1", clientCertificates: null,
                        enabledSslProtocols: SslProtocols.Tls11 | SslProtocols.Tls12,
                        checkCertificateRevocation: false);

                    var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n");
                    await sslStream.WriteAsync(request, 0, request.Length);
                    await sslStream.ReadAsync(new byte[32], 0, 32);
                }
            }

            await tcs.Task.TimeoutAfter(TimeSpan.FromSeconds(10));
        }

        private class HandshakeErrorLoggerFactory : ILoggerFactory
        {
            public HttpsConnectionFilterLogger FilterLogger { get; } = new HttpsConnectionFilterLogger();
            public ApplicationErrorLogger ErrorLogger { get; } = new ApplicationErrorLogger();

            public ILogger CreateLogger(string categoryName)
            {
                if (categoryName == nameof(HttpsConnectionFilter))
                {
                    return FilterLogger;
                }
                else
                {
                    return ErrorLogger;
                }
            }

            public void AddProvider(ILoggerProvider provider)
            {
                throw new NotImplementedException();
            }

            public void Dispose()
            {
            }
        }

        private class HttpsConnectionFilterLogger : ILogger
        {
            public LogLevel LastLogLevel { get; set; }
            public EventId LastEventId { get; set; }
            public TaskCompletionSource<object> LogTcs { get; } = new TaskCompletionSource<object>();

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                LastLogLevel = logLevel;
                LastEventId = eventId;
                Task.Run(() => LogTcs.SetResult(null));
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                throw new NotImplementedException();
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                throw new NotImplementedException();
            }
        }

        private class ApplicationErrorLogger : ILogger
        {
            public int TotalErrorsLogged { get; set; }

            public bool ObjectDisposedExceptionLogged { get; set; }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (logLevel == LogLevel.Error)
                {
                    TotalErrorsLogged++;
                }

                if (exception is ObjectDisposedException)
                {
                    ObjectDisposedExceptionLogged = true;
                }
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                return NullScope.Instance;
            }
        }
    }
}
