﻿// <copyright file="InMemoryPropTests.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Props.Dead;
using FubarDev.WebDavServer.Props.Store;
using FubarDev.WebDavServer.Tests.Support.ServiceBuilders;



using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace FubarDev.WebDavServer.Tests.PropertyStore
{
    public class InMemoryPropTests : IClassFixture<InMemoryFileSystemServices>, IDisposable
    {
        private readonly IServiceScope _serviceScope;

        public InMemoryPropTests(InMemoryFileSystemServices fsServices)
        {
            IServiceScopeFactory serviceScopeFactory = fsServices.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
            _serviceScope = serviceScopeFactory.CreateScope();
            FileSystem = _serviceScope.ServiceProvider.GetRequiredService<IFileSystem>();
            Dispatcher = _serviceScope.ServiceProvider.GetRequiredService<IWebDavDispatcher>();
        }

        public IWebDavDispatcher Dispatcher { get; }

        public IFileSystem FileSystem { get; }


        public IPropertyStore PropertyStore
        {
            get
            {
                Assert.NotNull(FileSystem.PropertyStore);
                return FileSystem.PropertyStore;
            }
        }

        [Fact]
        public async Task Empty()
        {
            CancellationToken ct = CancellationToken.None;
            ICollection root = await FileSystem.Root;
            DisplayNameProperty displayNameProperty = await GetDisplayNamePropertyAsync(root, ct).ConfigureAwait(false);
            Assert.Equal(string.Empty, await displayNameProperty.GetValueAsync(ct).ConfigureAwait(false));
        }

        [Fact]
        public async Task DocumentWithExtension()
        {
            CancellationToken ct = CancellationToken.None;

            ICollection root = await FileSystem.Root;
            IDocument doc = await root.CreateDocumentAsync("test1.txt", ct).ConfigureAwait(false);

            DisplayNameProperty displayNameProperty = await GetDisplayNamePropertyAsync(doc, ct).ConfigureAwait(false);
            Assert.Equal("test1.txt", await displayNameProperty.GetValueAsync(ct).ConfigureAwait(false));
        }

        [Fact]
        public async Task SameNameDocumentsInDifferentCollections()
        {
            CancellationToken ct = CancellationToken.None;

            ICollection root = await FileSystem.Root;
            ICollection coll1 = await root.CreateCollectionAsync("coll1", ct).ConfigureAwait(false);
            IDocument docRoot = await root.CreateDocumentAsync("test1.txt", ct).ConfigureAwait(false);
            IDocument docColl1 = await coll1.CreateDocumentAsync("test1.txt", ct).ConfigureAwait(false);
            Model.Headers.EntityTag eTagDocRoot = await PropertyStore.GetETagAsync(docRoot, ct).ConfigureAwait(false);
            Model.Headers.EntityTag eTagDocColl1 = await PropertyStore.GetETagAsync(docColl1, ct).ConfigureAwait(false);
            Assert.NotEqual(eTagDocRoot, eTagDocColl1);
        }

        [Fact]
        public async Task DisplayNameChangeable()
        {
            CancellationToken ct = CancellationToken.None;

            ICollection root = await FileSystem.Root;
            IDocument doc = await root.CreateDocumentAsync("test1.txt", ct).ConfigureAwait(false);
            DisplayNameProperty displayNameProperty = await GetDisplayNamePropertyAsync(doc, ct).ConfigureAwait(false);

            await displayNameProperty.SetValueAsync("test1-Dokument", ct).ConfigureAwait(false);
            Assert.Equal("test1-Dokument", await displayNameProperty.GetValueAsync(ct).ConfigureAwait(false));

            displayNameProperty = await GetDisplayNamePropertyAsync(doc, ct).ConfigureAwait(false);
            Assert.Equal("test1-Dokument", await displayNameProperty.GetValueAsync(ct).ConfigureAwait(false));
        }

        [Theory]
        [InlineData("test1.txt", "text/plain")]
        [InlineData("test1.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
        [InlineData("test1.png", "image/png")]
        public async Task ContentTypeDetected(string fileName, string contentType)
        {
            CancellationToken ct = CancellationToken.None;

            ICollection root = await FileSystem.Root;
            IDocument doc = await root.CreateDocumentAsync(fileName, ct).ConfigureAwait(false);
            GetContentTypeProperty contentTypeProperty = await GetContentTypePropertyAsync(doc, ct).ConfigureAwait(false);

            Assert.Equal(contentType, await contentTypeProperty.GetValueAsync(ct).ConfigureAwait(false));
        }

        public void Dispose()
        {
            _serviceScope.Dispose();
        }

        private async Task<DisplayNameProperty> GetDisplayNamePropertyAsync(IEntry entry, CancellationToken ct)
        {
            Props.IUntypedReadableProperty untypedDisplayNameProperty = await entry.GetProperties(Dispatcher).SingleAsync(x => x.Name == DisplayNameProperty.PropertyName, ct).ConfigureAwait(false);
            Assert.NotNull(untypedDisplayNameProperty);
            DisplayNameProperty displayNameProperty = Assert.IsType<DisplayNameProperty>(untypedDisplayNameProperty);
            return displayNameProperty;
        }

        private async Task<GetContentTypeProperty> GetContentTypePropertyAsync(IEntry entry, CancellationToken ct)
        {
            Props.IUntypedReadableProperty untypedContentTypeProperty = await entry.GetProperties(Dispatcher).SingleAsync(x => x.Name == GetContentTypeProperty.PropertyName, ct).ConfigureAwait(false);
            Assert.NotNull(untypedContentTypeProperty);
            GetContentTypeProperty contentTypeProperty = Assert.IsType<GetContentTypeProperty>(untypedContentTypeProperty);
            return contentTypeProperty;
        }
    }
}
