// <copyright file="LockManagerBase.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

#define USE_VARIANT_2

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Model;
using FubarDev.WebDavServer.Model.Headers;



using Microsoft.Extensions.Logging;

namespace FubarDev.WebDavServer.Locking
{
    /// <summary>
    /// The base implementation for an <see cref="ILockManager"/>
    /// </summary>
    /// <remarks>
    /// The derived class must implement <see cref="BeginTransactionAsync"/> and
    /// return an object that implements <see cref="ILockManagerTransaction"/>.
    /// </remarks>
    public abstract class LockManagerBase : ILockManager
    {
        private static readonly Uri _baseUrl = new("http://localhost/");
        private readonly ISystemClock _systemClock;


        private readonly ILogger _logger;


        private readonly ILockTimeRounding _rounding;

        /// <summary>
        /// Initializes a new instance of the <see cref="LockManagerBase"/> class.
        /// </summary>
        /// <param name="cleanupTask">The clean-up task for expired locks</param>
        /// <param name="systemClock">The system clock interface</param>
        /// <param name="logger">The logger</param>
        /// <param name="options">The options of the lock manager</param>
        protected LockManagerBase(ILockCleanupTask cleanupTask, ISystemClock systemClock, ILogger logger, ILockManagerOptions options = null)
        {
            _rounding = options?.Rounding ?? new DefaultLockTimeRounding(DefaultLockTimeRoundingMode.OneSecond);
            LockCleanupTask = cleanupTask;
            _systemClock = systemClock;
            _logger = logger;
        }

        /// <inheritdoc />
        public event EventHandler<LockEventArgs> LockAdded;

        /// <inheritdoc />
        public event EventHandler<LockEventArgs> LockReleased;

        private enum LockCompareResult
        {
            RightIsParent,
            LeftIsParent,
            Reference,
            NoMatch,
        }

        /// <summary>
        /// This interface must be implemented by the inheriting class.
        /// </summary>
        protected interface ILockManagerTransaction : IDisposable
        {
            /// <summary>
            /// Gets all active locks
            /// </summary>
            /// <param name="cancellationToken">The cancellation token</param>
            /// <returns>The collection of all active locks</returns>


            Task<IReadOnlyCollection<IActiveLock>> GetActiveLocksAsync(CancellationToken cancellationToken);

            /// <summary>
            /// Adds a new active lock
            /// </summary>
            /// <param name="activeLock">The active lock to add</param>
            /// <param name="cancellationToken">The cancellation token</param>
            /// <returns><see langword="true"/> when adding the lock succeeded</returns>

            Task<bool> AddAsync(IActiveLock activeLock, CancellationToken cancellationToken);

            /// <summary>
            /// Updates the active lock
            /// </summary>
            /// <param name="activeLock">The active lock with the updated values</param>
            /// <param name="cancellationToken">The cancellation token</param>
            /// <returns><see langword="true"/> when the lock was updatet, <see langword="false"/> when the lock was added instead</returns>

            Task<bool> UpdateAsync(IActiveLock activeLock, CancellationToken cancellationToken);

            /// <summary>
            /// Removes an active lock with the given <paramref name="stateToken"/>
            /// </summary>
            /// <param name="stateToken">The state token of the active lock to remove</param>
            /// <param name="cancellationToken">The cancellation token</param>
            /// <returns><see langword="true"/> when a lock with the given <paramref name="stateToken"/> existed and could be removed</returns>

            Task<bool> RemoveAsync(string stateToken, CancellationToken cancellationToken);

            /// <summary>
            /// Gets an active lock by its <paramref name="stateToken"/>
            /// </summary>
            /// <param name="stateToken">The state token to search for</param>
            /// <param name="cancellationToken">The cancellation token</param>
            /// <returns>The active lock for the state token or <see langword="null"/> when the lock wasn't found</returns>


            Task<IActiveLock> GetAsync(string stateToken, CancellationToken cancellationToken);

            /// <summary>
            /// Commits the changes made during the transaction
            /// </summary>
            /// <param name="cancellationToken">The cancellation token</param>
            /// <returns>The async task</returns>

            Task CommitAsync(CancellationToken cancellationToken);
        }

        /// <inheritdoc />
        public int Cost { get; } = 0;

        /// <summary>
        /// Gets the lock cleanup task
        /// </summary>

        protected ILockCleanupTask LockCleanupTask { get; }

        /// <inheritdoc />
        public async Task<LockResult> LockAsync(ILock l, CancellationToken cancellationToken)
        {
            ActiveLock newActiveLock;
            Uri destinationUrl = BuildUrl(l.Path);
            using (ILockManagerTransaction transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyCollection<IActiveLock> locks = await transaction.GetActiveLocksAsync(cancellationToken).ConfigureAwait(false);
                LockStatus status = Find(locks, destinationUrl, l.Recursive, true);
                LockStatus conflictingLocks = GetConflictingLocks(status, LockShareMode.Parse(l.ShareMode));
                if (conflictingLocks.Count != 0)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation($"Found conflicting locks for {l}: {string.Join(",", conflictingLocks.GetLocks().Select(x => x.ToString()))}");
                    }

                    return new LockResult(conflictingLocks);
                }

                newActiveLock = new ActiveLock(l, _rounding.Round(_systemClock.UtcNow), _rounding.Round(l.Timeout));

                await transaction.AddAsync(newActiveLock, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            OnLockAdded(newActiveLock);

            LockCleanupTask.Add(this, newActiveLock);

            return new LockResult(newActiveLock);
        }

        /// <inheritdoc />
        public async Task<IImplicitLock> LockImplicitAsync(
            IFileSystem rootFileSystem,
            IReadOnlyCollection<IfHeaderList> ifHeaderLists,
            ILock lockRequirements,
            CancellationToken cancellationToken)
        {
            if (ifHeaderLists == null || ifHeaderLists.Count == 0)
            {
                LockResult newLock = await LockAsync(lockRequirements, cancellationToken).ConfigureAwait(false);
                return new ImplicitLock(this, newLock);
            }

            IReadOnlyCollection<Tuple<PathInfo, IfHeaderList>> successfulConditions = await FindMatchingIfConditionListAsync(
                rootFileSystem,
                ifHeaderLists,
                lockRequirements,
                cancellationToken).ConfigureAwait(false);
            if (successfulConditions == null)
            {
                // No if conditions found for the requested path
                LockResult newLock = await LockAsync(lockRequirements, cancellationToken).ConfigureAwait(false);
                return new ImplicitLock(this, newLock);
            }

            Tuple<PathInfo, IfHeaderList> firstConditionWithStateToken = successfulConditions.FirstOrDefault(x => x.Item2.RequiresStateToken);
            if (firstConditionWithStateToken != null)
            {
                // Returns the list of locks matched by the first if list
                List<IActiveLock> usedLocks = firstConditionWithStateToken
                    .Item2.Conditions.Where(x => x.StateToken != null && !x.Not)
                    .Select(x => firstConditionWithStateToken.Item1.TokenToLock[x.StateToken]).ToList();
                return new ImplicitLock(usedLocks);
            }

            if (successfulConditions.Count != 0)
            {
                // At least one "If" header condition was successful, but we didn't find any with a state token
                LockResult newLock = await LockAsync(lockRequirements, cancellationToken).ConfigureAwait(false);
                return new ImplicitLock(this, newLock);
            }

            return new ImplicitLock();
        }

        /// <inheritdoc />
        public async Task<LockRefreshResult> RefreshLockAsync(IFileSystem rootFileSystem, IfHeader ifHeader, TimeSpan timeout, CancellationToken cancellationToken)
        {
            HashSet<Uri> failedHrefs = new();
            List<ActiveLock> refreshedLocks = new();

            Dictionary<Uri, PathInfo> pathToInfo = new();
            foreach (IfHeaderList ifHeaderList in ifHeader.Lists.Where(x => x.RequiresStateToken))
            {
                if (!pathToInfo.TryGetValue(ifHeaderList.Path, out PathInfo pathInfo))
                {
                    pathInfo = new PathInfo();
                    pathToInfo.Add(ifHeaderList.Path, pathInfo);
                }

                if (pathInfo.EntityTag == null)
                {
                    if (ifHeaderList.RequiresEntityTag)
                    {
                        SelectionResult selectionResult = await rootFileSystem.SelectAsync(ifHeaderList.Path.OriginalString, cancellationToken).ConfigureAwait(false);
                        if (selectionResult.IsMissing)
                        {
                            // Probably locked entry not found
                            continue;
                        }

                        pathInfo.EntityTag = await selectionResult.TargetEntry.GetEntityTagAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            using (ILockManagerTransaction transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (IfHeaderList ifHeaderList in ifHeader.Lists.Where(x => x.RequiresStateToken))
                {
                    PathInfo pathInfo = pathToInfo[ifHeaderList.Path];

                    if (pathInfo.ActiveLocks == null)
                    {
                        Uri destinationUrl = BuildUrl(ifHeaderList.Path.OriginalString);
                        List<IActiveLock> entryLocks = (from l in await transaction.GetActiveLocksAsync(cancellationToken).ConfigureAwait(false)
                                                        let lockUrl = BuildUrl(l.Path)
                                                        where Compare(destinationUrl, false, lockUrl, false) == LockCompareResult.Reference
                                                        select l).ToList();

                        if (entryLocks.Count == 0)
                        {
                            // No lock found for entry
                            failedHrefs.Add(ifHeaderList.RelativeHref);
                            continue;
                        }

                        pathInfo.ActiveLocks = entryLocks;
                        pathInfo.TokenToLock = entryLocks.ToDictionary(x => new Uri(x.StateToken, UriKind.RelativeOrAbsolute));
                        pathInfo.LockTokens = pathInfo.TokenToLock.Keys.ToList();
                    }

                    IActiveLock foundLock = pathInfo.TokenToLock.Where(x => ifHeaderList.IsMatch(pathInfo.EntityTag, new[] { x.Key })).Select(x => x.Value).SingleOrDefault();
                    if (foundLock != null)
                    {
                        ActiveLock refreshedLock = Refresh(foundLock, _rounding.Round(_systemClock.UtcNow), _rounding.Round(timeout));

                        // Remove old lock from clean-up task
                        LockCleanupTask.Remove(foundLock);

                        refreshedLocks.Add(refreshedLock);
                    }
                    else
                    {
                        failedHrefs.Add(ifHeaderList.RelativeHref);
                    }
                }

                if (refreshedLocks.Count == 0)
                {
                    List<Uri> hrefs = failedHrefs.ToList();
                    string href = hrefs.First().OriginalString;
                    object[] hrefItems = hrefs.Skip(1).Select(x => x.OriginalString).Cast<object>().ToArray();
                    ItemsChoiceType2[] hrefItemNames = hrefItems.Select(x => ItemsChoiceType2.href).ToArray();

                    return new LockRefreshResult(
                        new response()
                        {
                            href = href,
                            Items = hrefItems,
                            ItemsElementName = hrefItemNames,
                            error = new error()
                            {
                                Items = new[] { new object(), },
                                ItemsElementName = new[] { ItemsChoiceType.locktokenmatchesrequesturi, },
                            },
                        });
                }

                foreach (ActiveLock newLock in refreshedLocks)
                {
                    await transaction.UpdateAsync(newLock, cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (ActiveLock newLock in refreshedLocks)
            {
                LockCleanupTask.Add(this, newLock);
            }

            return new LockRefreshResult(refreshedLocks);
        }

        /// <inheritdoc />
        public async Task<LockReleaseStatus> ReleaseAsync(string path, Uri stateToken, CancellationToken cancellationToken)
        {
            IActiveLock activeLock;
            using (ILockManagerTransaction transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                activeLock = await transaction.GetAsync(stateToken.OriginalString, cancellationToken).ConfigureAwait(false);
                if (activeLock == null)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation($"Tried to remove non-existent lock {stateToken}");
                    }

                    return LockReleaseStatus.NoLock;
                }

                Uri destinationUrl = BuildUrl(path);
                Uri lockUrl = BuildUrl(activeLock.Path);
                LockCompareResult lockCompareResult = Compare(lockUrl, activeLock.Recursive, destinationUrl, false);
                if (lockCompareResult != LockCompareResult.Reference)
                {
                    return LockReleaseStatus.InvalidLockRange;
                }

                await transaction.RemoveAsync(stateToken.OriginalString, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            LockCleanupTask.Remove(activeLock);

            OnLockReleased(activeLock);

            return LockReleaseStatus.Success;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<IActiveLock>> GetLocksAsync(CancellationToken cancellationToken)
        {
            using ILockManagerTransaction transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyCollection<IActiveLock> locks = await transaction.GetActiveLocksAsync(cancellationToken).ConfigureAwait(false);
            return locks;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<IActiveLock>> GetAffectedLocksAsync(string path, bool findChildren, bool findParents, CancellationToken cancellationToken)
        {
            Uri destinationUrl = BuildUrl(path);
            LockStatus status;
            using (ILockManagerTransaction transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyCollection<IActiveLock> locks = await transaction.GetActiveLocksAsync(cancellationToken).ConfigureAwait(false);
                status = Find(locks, destinationUrl, findChildren, findParents);
            }

            return status.ParentLocks.Concat(status.ReferenceLocks).Concat(status.ChildLocks);
        }

        /// <summary>
        /// Converts a client path to a system path.
        /// </summary>
        /// <remarks>
        /// <para>The client path has the form <c>http://localhost/root-file-system/relative/path</c> and is
        /// therefore always an absolute path. The returned path must be absolute too and might have
        /// the form <c>http://localhost/c/relative/path</c> or something similar. It is of utmost
        /// importance that the URI is always stable. The default implementation of this function
        /// doesn't make any conversions, because it assumes that the same path path always points
        /// to the same file system entry for all clients.</para>
        /// <para>
        /// A URI to a directory must always end in a slash (<c>/</c>).
        /// </para>
        /// </remarks>
        /// <param name="path">The client path to convert</param>
        /// <returns>The system path to be converted to</returns>
        protected virtual Uri NormalizePath(Uri path)
        {
            return path;
        }

        /// <summary>
        /// Gets called when a lock was added
        /// </summary>
        /// <param name="activeLock">The lock that was added</param>
        protected virtual void OnLockAdded(IActiveLock activeLock)
        {
            LockAdded?.Invoke(this, new LockEventArgs(activeLock));
        }

        /// <summary>
        /// Gets called when a lock was released
        /// </summary>
        /// <param name="activeLock">The lock that was released</param>
        protected virtual void OnLockReleased(IActiveLock activeLock)
        {
            LockReleased?.Invoke(this, new LockEventArgs(activeLock));
        }

        /// <summary>
        /// Begins a new transaction
        /// </summary>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>The transaction to be used to update the active locks</returns>
        protected abstract Task<ILockManagerTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

        private static LockStatus GetConflictingLocks(LockStatus affactingLocks, LockShareMode shareMode)
        {
            return shareMode == LockShareMode.Exclusive
                ? affactingLocks
                : new LockStatus(
                affactingLocks
                    .ReferenceLocks
                    .Where(x => LockShareMode.Parse(x.ShareMode) == LockShareMode.Exclusive)
                    .ToList(),
                affactingLocks
                    .ParentLocks
                    .Where(x => LockShareMode.Parse(x.ShareMode) == LockShareMode.Exclusive)
                    .ToList(),
                affactingLocks
                    .ChildLocks
                    .Where(x => LockShareMode.Parse(x.ShareMode) == LockShareMode.Exclusive)
                    .ToList());
        }

        /// <summary>
        /// Returns a new active lock whose new expiration date/time is recalculated using <paramref name="lastRefresh"/> and <paramref name="timeout"/>.
        /// </summary>
        /// <param name="activeLock">The active lock to refresh</param>
        /// <param name="lastRefresh">The date/time of the last refresh</param>
        /// <param name="timeout">The new timeout to apply to the lock</param>
        /// <returns>The new (refreshed) active lock</returns>
        [Pure]
        private static ActiveLock Refresh(IActiveLock activeLock, DateTime lastRefresh, TimeSpan timeout)
        {
            return new ActiveLock(
                activeLock.Path,
                activeLock.Href,
                activeLock.Recursive,
                activeLock.GetOwner(),
                LockAccessType.Parse(activeLock.AccessType),
                LockShareMode.Parse(activeLock.ShareMode),
                timeout,
                activeLock.Issued,
                lastRefresh,
                activeLock.StateToken);
        }

#if USE_VARIANT_1
        
        
        private async Task<IReadOnlyCollection<Tuple<PathInfo, IfHeaderList>>> FindMatchingIfConditionListAsync(
             IFileSystem rootFileSystem,
              IReadOnlyCollection<IfHeaderList> ifHeaderLists,
             ILock lockRequirements,
            CancellationToken cancellationToken)
        {
            var lockRequirementUrl = BuildUrl(lockRequirements.Path);

            var supportedIfConditions = new List<IfHeaderList>();
            var pathToInfo = new Dictionary<Uri, PathInfo>();
            foreach (var ifHeaderList in ifHeaderLists)
            {
                var ifHeaderUrl = BuildUrl(ifHeaderList.Path.OriginalString);
                var headerCompareResult = Compare(ifHeaderUrl, true, lockRequirementUrl, false);
                if (headerCompareResult != LockCompareResult.LeftIsParent &&
                    headerCompareResult != LockCompareResult.Reference)
                    continue;

                supportedIfConditions.Add(ifHeaderList);

                PathInfo pathInfo;
                if (!pathToInfo.TryGetValue(ifHeaderList.Path, out pathInfo))
                {
                    pathInfo = new PathInfo();
                    pathToInfo.Add(ifHeaderList.Path, pathInfo);
                }

                if (pathInfo.EntityTag == null)
                {
                    if (ifHeaderList.RequiresEntityTag)
                    {
                        var selectionResult = await rootFileSystem
                            .SelectAsync(ifHeaderList.Path.OriginalString, cancellationToken).ConfigureAwait(false);
                        if (selectionResult.IsMissing)
                        {
                            // Probably locked entry not found
                            continue;
                        }

                        pathInfo.EntityTag = await selectionResult
                            .TargetEntry.GetEntityTagAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (supportedIfConditions.Count == 0)
                return null;

            var successfulConditions = new List<Tuple<PathInfo, IfHeaderList>>();
            lock (_syncRoot)
            {
                foreach (var ifHeaderList in supportedIfConditions)
                {
                    var pathInfo = pathToInfo[ifHeaderList.Path];

                    if (pathInfo.ActiveLocks == null)
                    {
                        var destinationUrl = BuildUrl(ifHeaderList.Path.OriginalString);
                        var entryLocks = Find(destinationUrl, false).GetLocks().ToList();
                        pathInfo.ActiveLocks = entryLocks;
                        pathInfo.TokenToLock = entryLocks.ToDictionary(x => new Uri(x.StateToken, UriKind.RelativeOrAbsolute));
                        pathInfo.LockTokens = pathInfo.TokenToLock.Keys.ToList();
                    }

                    if (ifHeaderList.IsMatch(pathInfo.EntityTag, pathInfo.LockTokens))
                    {
                        successfulConditions.Add(Tuple.Create(pathInfo, ifHeaderList));
                    }
                }
            }

            return successfulConditions;
        }
#endif

#if USE_VARIANT_2


        private async Task<IReadOnlyCollection<Tuple<PathInfo, IfHeaderList>>> FindMatchingIfConditionListAsync(
             IFileSystem rootFileSystem,
              IReadOnlyCollection<IfHeaderList> ifHeaderLists,
             ILock lockRequirements,
            CancellationToken cancellationToken)
        {
            Uri lockRequirementUrl = BuildUrl(lockRequirements.Path);

            IReadOnlyCollection<IActiveLock> affectingLocks;
            using (ILockManagerTransaction transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyCollection<IActiveLock> locks = await transaction.GetActiveLocksAsync(cancellationToken).ConfigureAwait(false);
                LockStatus lockStatus = Find(locks, lockRequirementUrl, false, true);
                affectingLocks = lockStatus.ParentLocks.Concat(lockStatus.ReferenceLocks).ToList();
            }

            // Get all If header lists together with all relevant active locks
            Dictionary<IfHeaderList, IReadOnlyCollection<IActiveLock>> ifListLocks =
                (from list in ifHeaderLists
                 let listUrl = BuildUrl(list.Path.OriginalString)
                 let compareResult = Compare(listUrl, true, lockRequirementUrl, false)
                 where compareResult is LockCompareResult.LeftIsParent
                       or LockCompareResult.Reference
                 let foundLocks = list.RequiresStateToken
                     ? Find(affectingLocks, listUrl, compareResult == LockCompareResult.LeftIsParent, true)
                     : LockStatus.Empty
                 let locksForIfConditions = foundLocks.GetLocks().ToList()
                 select Tuple.Create<IfHeaderList, IReadOnlyCollection<IActiveLock>>(list, locksForIfConditions))
                .ToDictionary(x => x.Item1, x => x.Item2);

            // List of matches between path info and if header lists
            List<Tuple<PathInfo, IfHeaderList>> successfulConditions = new();
            if (ifListLocks.Count == 0)
            {
                return null;
            }

            // Collect all file system specific information
            Dictionary<Uri, PathInfo> pathToInfo = new();
            foreach (KeyValuePair<IfHeaderList, IReadOnlyCollection<IActiveLock>> matchingIfListItem in ifListLocks)
            {
                IfHeaderList ifHeaderList = matchingIfListItem.Key;
                if (!pathToInfo.TryGetValue(ifHeaderList.Path, out PathInfo pathInfo))
                {
                    pathInfo = new PathInfo
                    {
                        ActiveLocks = matchingIfListItem.Value,
                        TokenToLock = matchingIfListItem
                            .Value.ToDictionary(x => new Uri(x.StateToken, UriKind.RelativeOrAbsolute), x => x),
                    };
                    pathInfo.LockTokens = pathInfo.TokenToLock.Keys.ToList();
                    pathToInfo.Add(ifHeaderList.Path, pathInfo);
                }

                if (pathInfo.EntityTag == null)
                {
                    if (ifHeaderList.RequiresEntityTag)
                    {
                        SelectionResult selectionResult = await rootFileSystem
                            .SelectAsync(ifHeaderList.Path.OriginalString, cancellationToken).ConfigureAwait(false);
                        if (!selectionResult.IsMissing)
                        {
                            pathInfo.EntityTag = await selectionResult
                                .TargetEntry.GetEntityTagAsync(cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }

                if (ifHeaderList.IsMatch(pathInfo.EntityTag, pathInfo.LockTokens))
                {
                    successfulConditions.Add(Tuple.Create(pathInfo, ifHeaderList));
                }
            }

            return successfulConditions;
        }
#endif

        private LockStatus Find(IEnumerable<IActiveLock> locks, Uri parentUrl, bool withChildren, bool findParents)
        {
            Uri normalizedParentUrl = NormalizePath(parentUrl);
            List<IActiveLock> refLocks = new();
            List<IActiveLock> childLocks = new();
            List<IActiveLock> parentLocks = new();

            foreach (IActiveLock activeLock in locks)
            {
                Uri lockUrl = BuildUrl(activeLock.Path);
                Uri normalizedLockUrl = NormalizePath(lockUrl);
                LockCompareResult result = Compare(normalizedParentUrl, withChildren, normalizedLockUrl, activeLock.Recursive);
                switch (result)
                {
                    case LockCompareResult.Reference:
                        refLocks.Add(activeLock);
                        break;
                    case LockCompareResult.LeftIsParent:
                        childLocks.Add(activeLock);
                        break;
                    case LockCompareResult.RightIsParent:
                        if (findParents)
                        {
                            parentLocks.Add(activeLock);
                        }

                        break;
                }
            }

            return new LockStatus(refLocks, parentLocks, childLocks);
        }

        private LockCompareResult Compare(Uri left, bool leftRecursive, Uri right, bool rightRecursive)
        {
            if (left == right)
            {
                return LockCompareResult.Reference;
            }

            if (left.IsBaseOf(right) && leftRecursive)
            {
                return LockCompareResult.LeftIsParent;
            }

            return right.IsBaseOf(left) && rightRecursive ? LockCompareResult.RightIsParent : LockCompareResult.NoMatch;
        }

        private Uri BuildUrl(string path)
        {
            return string.IsNullOrEmpty(path) ? _baseUrl : new Uri(_baseUrl, path + (path.EndsWith("/") ? string.Empty : "/"));
        }

        private class PathInfo
        {
            public EntityTag? EntityTag { get; set; }

            public IReadOnlyCollection<IActiveLock> ActiveLocks { get; set; }

            public IDictionary<Uri, IActiveLock> TokenToLock { get; set; }

            public IReadOnlyCollection<Uri> LockTokens { get; set; }
        }
    }
}
