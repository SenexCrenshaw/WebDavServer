// <copyright file="SQLiteLockServices.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.Collections.Concurrent;
using System.IO;

using FubarDev.WebDavServer.Locking;
using FubarDev.WebDavServer.Locking.SQLite;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FubarDev.WebDavServer.Tests.Support.ServiceBuilders
{
    public class SQLiteLockServices : ILockServices, IDisposable
    {
        private readonly ConcurrentBag<string> _tempDbFileNames = [];

        public SQLiteLockServices()
        {
            ServiceCollection serviceCollection = new();
            serviceCollection.AddOptions();
            serviceCollection.AddLogging();
            serviceCollection.AddScoped<ISystemClock, TestSystemClock>();
            serviceCollection.AddTransient<ILockCleanupTask, LockCleanupTask>();
            serviceCollection.AddTransient<ILockManager>(
                sp =>
                {
                    string tempDbFileName = Path.GetTempFileName();
                    _tempDbFileNames.Add(tempDbFileName);
                    SQLiteLockManagerOptions config = new()
                    {
                        Rounding = new DefaultLockTimeRounding(DefaultLockTimeRoundingMode.OneHundredMilliseconds),
                        DatabaseFileName = tempDbFileName,
                    };
                    ILockCleanupTask cleanupTask = sp.GetRequiredService<ILockCleanupTask>();
                    ISystemClock systemClock = sp.GetRequiredService<ISystemClock>();
                    ILogger<SQLiteLockManager> logger = sp.GetRequiredService<ILogger<SQLiteLockManager>>();
                    return new SQLiteLockManager(config, cleanupTask, systemClock, logger);
                });
            ServiceProvider = serviceCollection.BuildServiceProvider();

            ILoggingBuilder loggerFactory = ServiceProvider.GetRequiredService<ILoggingBuilder>();
            loggerFactory.AddDebug();
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose()
        {
            foreach (string tempDbFileName in _tempDbFileNames)
            {
                File.Delete(tempDbFileName);
            }
        }
    }
}
