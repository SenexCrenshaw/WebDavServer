// <copyright file="MemoryLockServices.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;

using FubarDev.WebDavServer.Locking;
using FubarDev.WebDavServer.Locking.InMemory;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FubarDev.WebDavServer.Tests.Support.ServiceBuilders
{
    public class MemoryLockServices : ILockServices
    {
        public MemoryLockServices()
        {
            ServiceCollection serviceCollection = new();
            serviceCollection.AddOptions();
            serviceCollection.AddLogging();
            serviceCollection.AddScoped<ISystemClock, TestSystemClock>();
            serviceCollection.Configure<InMemoryLockManagerOptions>(opt =>
            {
                opt.Rounding = new DefaultLockTimeRounding(DefaultLockTimeRoundingMode.OneHundredMilliseconds);
            });
            serviceCollection.AddTransient<ILockCleanupTask, LockCleanupTask>();
            serviceCollection.AddTransient<ILockManager, InMemoryLockManager>();
            ServiceProvider = serviceCollection.BuildServiceProvider();

            ILoggingBuilder loggerBuilder = ServiceProvider.GetRequiredService<ILoggingBuilder>();
            loggerBuilder.AddDebug();
        }

        public IServiceProvider ServiceProvider { get; }
    }
}
