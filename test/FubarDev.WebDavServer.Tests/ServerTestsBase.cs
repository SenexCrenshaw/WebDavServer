﻿// <copyright file="ServerTestsBase.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.Net.Http;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

using FubarDev.WebDavServer.AspNetCore;
using FubarDev.WebDavServer.Engines.Remote;
using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.FileSystem.InMemory;
using FubarDev.WebDavServer.Handlers.Impl;
using FubarDev.WebDavServer.Locking;
using FubarDev.WebDavServer.Locking.InMemory;
using FubarDev.WebDavServer.Props.Dead;
using FubarDev.WebDavServer.Props.Store.InMemory;
using FubarDev.WebDavServer.Tests.Support;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using WebDav;

namespace FubarDev.WebDavServer.Tests
{
    public class ServerTestsBase : IDisposable
    {
        protected ServerTestsBase(RecursiveProcessingMode processingMode)
        {
            FileSystem = new InMemoryFileSystem(
                new PathTraversalEngine(),
                new SystemClock(),
                new DeadPropertyFactory(),
                new InMemoryPropertyStoreFactory());
            var builder = new WebHostBuilder()
                .ConfigureServices(sc => ConfigureServices(this, processingMode, sc))
                .UseStartup<TestStartup>();
            Server = new TestServer(builder);
            Client = new WebDavClient(Server.CreateClient());
        }

        [NotNull]
        protected IFileSystem FileSystem { get; }

        [NotNull]
        protected TestServer Server { get; }

        [NotNull]
        protected WebDavClient Client { get; }

        [NotNull]
        protected IServiceProvider ServiceProvider => Server.Host.Services;

        public void Dispose()
        {
            Server.Dispose();
            Client.Dispose();
        }

        private void ConfigureServices(ServerTestsBase container, RecursiveProcessingMode processingMode, IServiceCollection services)
        {
            services
                .AddOptions()
                .AddLogging()
                .Configure<CopyHandlerOptions>(
                    opt =>
                    {
                        opt.Mode = processingMode;
                    })
                .Configure<MoveHandlerOptions>(
                    opt =>
                    {
                        opt.Mode = processingMode;
                    })
                .AddScoped<IWebDavContext>(sp => new TestHost(container.Server.BaseAddress, sp.GetRequiredService<IHttpContextAccessor>()))
                .AddScoped<IHttpMessageHandlerFactory>(sp => new TestHttpMessageHandlerFactory(container.Server))
                .AddSingleton<ILockManager, InMemoryLockManager>()
                .AddSingleton<IFileSystemFactory>(sp => new TestFileSystemFactory(container.FileSystem))
                .AddTransient(sp =>
                {
                    var factory = sp.GetRequiredService<IFileSystemFactory>();
                    var context = sp.GetRequiredService<IHttpContextAccessor>();
                    return factory.CreateFileSystem(context.HttpContext.User.Identity);
                })
                .AddMvcCore()
                .AddWebDav();
        }

        [UsedImplicitly]
        private class TestStartup
        {
            [UsedImplicitly]
            public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
            {
                loggerFactory.AddDebug();
                app.UseMvc();
            }
        }

        private class TestFileSystemFactory : IFileSystemFactory
        {
            private readonly IFileSystem _fileSystem;

            public TestFileSystemFactory(IFileSystem fileSystem)
            {
                _fileSystem = fileSystem;
            }

            public IFileSystem CreateFileSystem(IIdentity identity)
            {
                return _fileSystem;
            }
        }

        private class TestHttpMessageHandlerFactory : IHttpMessageHandlerFactory
        {
            private readonly TestServer _server;

            public TestHttpMessageHandlerFactory(TestServer server)
            {
                _server = server;
            }

            public Task<HttpMessageHandler> CreateAsync(Uri baseUrl, CancellationToken cancellationToken)
            {
                return Task.FromResult(_server.CreateHandler());
            }
        }
    }
}
