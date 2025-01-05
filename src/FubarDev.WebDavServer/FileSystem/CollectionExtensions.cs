// <copyright file="CollectionExtensions.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using FubarDev.WebDavServer.FileSystem.Mount;



namespace FubarDev.WebDavServer.FileSystem
{
    /// <summary>
    /// Extension methods for the collections
    /// </summary>
    public static class CollectionExtensions
    {
        /// <summary>
        /// Returns the target if the collection is a mount point or the collection itself
        /// </summary>
        /// <param name="collection">The collection to found the mount destination for</param>
        /// <param name="mountPointProvider">The mount point provider</param>
        /// <returns>The <paramref name="collection"/> or the destination collection if a mount point existed</returns>
        public static async Task<ICollection> GetMountTargetAsync(this ICollection collection, IMountPointProvider mountPointProvider)
        {
            return mountPointProvider != null && mountPointProvider.TryGetMountPoint(collection.Path, out IFileSystem fileSystem)
                ? await fileSystem.Root
                : collection;
        }

        /// <summary>
        /// Returns the target if the collection is a mount point or the collection itself
        /// </summary>
        /// <param name="collection">The collection to found the mount destination for</param>
        /// <param name="mountPointProvider">The mount point provider</param>
        /// <returns>The <paramref name="collection"/> or the destination collection if a mount point existed</returns>
        public static async Task<IEntry> GetMountTargetEntryAsync(this ICollection collection, IMountPointProvider mountPointProvider)
        {
            return mountPointProvider != null && mountPointProvider.TryGetMountPoint(collection.Path, out IFileSystem fileSystem)
                ? await fileSystem.Root
                : (IEntry)collection;
        }

        /// <summary>
        /// Gets all entries of a collection recursively
        /// </summary>
        /// <param name="collection">The collection to get the entries from</param>
        /// <param name="children">Child items for the given <paramref name="collection"/></param>
        /// <param name="maxDepth">The maximum depth (0 = only entries of the <paramref name="collection"/>, but not of its sub collections)</param>
        /// <returns>An async enumerable to collect all the entries recursively</returns>
        public static IAsyncEnumerable<IEntry> EnumerateEntries(this ICollection collection, IReadOnlyCollection<IEntry> children, int maxDepth)
        {
            return new FileSystemEntries(collection, children, 0, maxDepth);
        }

        /// <summary>
        /// Gets all entries of a collection recursively
        /// </summary>
        /// <param name="collection">The collection to get the entries from</param>
        /// <param name="maxDepth">The maximum depth (0 = only entries of the <paramref name="collection"/>, but not of its sub collections)</param>
        /// <returns>An async enumerable to collect all the entries recursively</returns>
        public static IAsyncEnumerable<IEntry> EnumerateEntries(this ICollection collection, int maxDepth)
        {
            return new FileSystemEntries(collection, null, 0, maxDepth);
        }

        /// <summary>
        /// Gets the collection as node
        /// </summary>
        /// <param name="collection">The collection to get the node for</param>
        /// <param name="maxDepth">The maximum depth to be used to get the child nodes</param>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>The collection node</returns>
        public static async Task<ICollectionNode> GetNodeAsync(this ICollection collection, int maxDepth, CancellationToken cancellationToken)
        {
            Queue<NodeInfo> subNodeQueue = new();
            NodeInfo result = new(collection);
            NodeInfo current = result;

            if (maxDepth > 0)
            {
                await using IAsyncEnumerator<IEntry> entries = EnumerateEntries(collection, maxDepth - 1).GetAsyncEnumerator(cancellationToken);
                while (await entries.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    IEntry entry = entries.Current;
                    ICollection parent = entry.Parent;
                    while (parent != current.Collection)
                    {
                        current = subNodeQueue.Dequeue();
                    }

                    if (entry is not IDocument doc)
                    {
                        ICollection coll = (ICollection)entry;
                        NodeInfo info = new(coll);
                        current.SubNodes.Add(info);
                        subNodeQueue.Enqueue(info);
                    }
                    else
                    {
                        current.Documents.Add(doc);
                    }
                }
            }

            return result;
        }

        private class FileSystemEntries : IAsyncEnumerable<IEntry>
        {
            private readonly ICollection _collection;

            private readonly IReadOnlyCollection<IEntry> _children;

            private readonly int _remainingDepth;

            private readonly int _startDepth;

            public FileSystemEntries(ICollection collection, IReadOnlyCollection<IEntry> children, int startDepth, int remainingDepth)
            {
                _collection = collection;
                _children = children;
                _startDepth = startDepth;
                _remainingDepth = remainingDepth;
            }

            public IAsyncEnumerator<IEntry> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                return new FileSystemEntriesEnumerator(_collection, _children, _startDepth, _remainingDepth);
            }

            //public IAsyncEnumerator<IEntry> GetEnumerator()
            //{
            //    return new FileSystemEntriesEnumerator(_collection, _children, _startDepth, _remainingDepth);
            //}

            private class FileSystemEntriesEnumerator : IAsyncEnumerator<IEntry>
            {
                private readonly Queue<CollectionInfo> _collections = new();

                private readonly int _maxDepth;

                private ICollection _collection;

                private int _currentDepth;

                private IEnumerator<IEntry> _entries;

                public FileSystemEntriesEnumerator(ICollection collection, IReadOnlyCollection<IEntry> children, int startDepth, int maxDepth)
                {
                    _maxDepth = maxDepth;
                    _currentDepth = startDepth;
                    _collections.Enqueue(new CollectionInfo(collection, children, startDepth));
                }

                public IEntry Current { get; private set; }

                public void Dispose()
                {
                    _entries?.Dispose();
                }

                public ValueTask DisposeAsync()
                {
                    _entries?.Dispose();
                    return ValueTask.CompletedTask;
                }

                public async ValueTask<bool> MoveNextAsync()
                {
                    bool resultFound = false;
                    bool hasCurrent = false;
                    CancellationToken cancellationToken = CancellationToken.None;

                    while (!resultFound)
                    {
                        //cancellationToken.ThrowIfCancellationRequested();

                        if (_entries == null)
                        {
                            CollectionInfo nextCollectionInfo = _collections.Dequeue();
                            _collection = nextCollectionInfo.Collection;
                            _currentDepth = nextCollectionInfo.Depth;
                            IReadOnlyCollection<IEntry> children = nextCollectionInfo.Children ?? await _collection.GetChildrenAsync(cancellationToken).ConfigureAwait(false);
                            _entries = children.GetEnumerator();
                        }

                        if (_entries.MoveNext())
                        {
                            if (_currentDepth < _maxDepth && _entries.Current is ICollection coll)
                            {
                                IReadOnlyCollection<IEntry> children;
                                try
                                {
                                    children = await coll.GetChildrenAsync(cancellationToken).ConfigureAwait(false);
                                }
                                catch (Exception)
                                {
                                    // Ignore errors
                                    children = new IEntry[0];
                                }

                                CollectionInfo collectionInfo = new(coll, children, _currentDepth + 1);
                                _collections.Enqueue(collectionInfo);
                            }

                            if (_currentDepth >= 0)
                            {
                                Current = _entries.Current;
                                resultFound = true;
                                hasCurrent = true;
                            }
                        }
                        else
                        {
                            Current = null;
                            _entries.Dispose();
                            _entries = null;
                            resultFound = _collections.Count == 0;
                        }
                    }

                    return hasCurrent;
                }

                //public ValueTask<bool> MoveNextAsync()
                //{
                //    throw new NotImplementedException();
                //}

                private readonly struct CollectionInfo
                {
                    public CollectionInfo(ICollection collection, IReadOnlyCollection<IEntry> children, int depth)
                    {
                        Collection = collection;
                        Children = children;
                        Depth = depth;
                    }


                    public ICollection Collection { get; }



                    public IReadOnlyCollection<IEntry> Children { get; }

                    public int Depth { get; }
                }
            }
        }

        private class NodeInfo : ICollectionNode
        {
            public NodeInfo(ICollection collection)
            {
                Collection = collection;
            }

            public string Name => Collection.Name;

            public ICollection Collection { get; }

            public List<IDocument> Documents { get; } = [];

            public List<NodeInfo> SubNodes { get; } = [];

            IReadOnlyCollection<ICollectionNode> ICollectionNode.Nodes => SubNodes;

            IReadOnlyCollection<IDocument> ICollectionNode.Documents => Documents;
        }
    }
}
