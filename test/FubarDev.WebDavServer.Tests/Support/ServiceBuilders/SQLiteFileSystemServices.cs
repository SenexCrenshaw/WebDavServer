﻿// <copyright file="SQLiteFileSystemServices.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.FileSystem.SQLite;
using FubarDev.WebDavServer.Locking;
using FubarDev.WebDavServer.Locking.InMemory;
using FubarDev.WebDavServer.Props.Dead;
using FubarDev.WebDavServer.Props.Store;
using FubarDev.WebDavServer.Props.Store.InMemory;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FubarDev.WebDavServer.Tests.Support.ServiceBuilders
{
    public class SQLiteFileSystemServices : IFileSystemServices, IDisposable
    {
        private readonly ConcurrentBag<string> _tempDbRootPaths = [];

        public SQLiteFileSystemServices()
        {
            IServiceCollection serviceCollection = new ServiceCollection()
                .AddOptions()
                .AddLogging()
                .Configure<InMemoryLockManagerOptions>(opt =>
                {
                    opt.Rounding = new DefaultLockTimeRounding(DefaultLockTimeRoundingMode.OneHundredMilliseconds);
                })
                .AddScoped<ILockManager, InMemoryLockManager>()
                .AddScoped<IDeadPropertyFactory, DeadPropertyFactory>()
                .AddScoped<IWebDavContext>(sp => new TestHost(sp, new Uri("http://localhost/")))
                .AddScoped<IFileSystemFactory, SQLiteFileSystemFactory>(
                    sp =>
                    {
                        string tempRootPath = Path.Combine(
                            Path.GetTempPath(),
                            "webdavserver-sqlite-tests",
                            Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempRootPath);
                        _tempDbRootPaths.Add(tempRootPath);

                        SQLiteFileSystemOptions opt = new()
                        {
                            RootPath = tempRootPath,
                        };
                        IPathTraversalEngine pte = sp.GetRequiredService<IPathTraversalEngine>();
                        IPropertyStoreFactory psf = sp.GetService<IPropertyStoreFactory>();
                        ILockManager lm = sp.GetService<ILockManager>();

                        SQLiteFileSystemFactory fsf = new(
                            new OptionsWrapper<SQLiteFileSystemOptions>(opt),
                            pte,
                            psf,
                            lm);
                        return fsf;
                    })
                .AddSingleton<IPropertyStoreFactory, InMemoryPropertyStoreFactory>()
                .AddWebDav();
            ServiceProvider = serviceCollection.BuildServiceProvider();

            ILoggingBuilder loggerFactory = ServiceProvider.GetRequiredService<ILoggingBuilder>();
            loggerFactory.AddDebug();
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose()
        {
            foreach (string tempDbRootPath in _tempDbRootPaths.Where(Directory.Exists))
            {
                Directory.Delete(tempDbRootPath, true);
            }
        }
    }
}
