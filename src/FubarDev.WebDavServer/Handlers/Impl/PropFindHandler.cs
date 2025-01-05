// <copyright file="PropFindHandler.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Model;
using FubarDev.WebDavServer.Model.Headers;
using FubarDev.WebDavServer.Props.Filters;
using FubarDev.WebDavServer.Props.Live;
using FubarDev.WebDavServer.Utils;



using Microsoft.Extensions.Options;

namespace FubarDev.WebDavServer.Handlers.Impl
{
    /// <summary>
    /// The implementation of the <see cref="IPropFindHandler"/> interface
    /// </summary>
    public class PropFindHandler : IPropFindHandler
    {

        private readonly IWebDavContext _context;


        private readonly PropFindHandlerOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="PropFindHandler"/> class.
        /// </summary>
        /// <param name="fileSystem">The root file system</param>
        /// <param name="context">The WebDAV request context</param>
        /// <param name="options">The options for this handler</param>
        public PropFindHandler(IFileSystem fileSystem, IWebDavContext context, IOptions<PropFindHandlerOptions> options)
        {
            _options = options?.Value ?? new PropFindHandlerOptions();
            _context = context;
            FileSystem = fileSystem;
        }

        /// <inheritdoc />
        public IEnumerable<string> HttpMethods { get; } = new[] { "PROPFIND" };

        /// <summary>
        /// Gets the root file system
        /// </summary>

        public IFileSystem FileSystem { get; }

        /// <inheritdoc />
        public async Task<IWebDavResult> PropFindAsync(string path, propfind request, CancellationToken cancellationToken)
        {
            SelectionResult selectionResult = await FileSystem.SelectAsync(path, cancellationToken).ConfigureAwait(false);
            if (selectionResult.IsMissing)
            {
                if (_context.RequestHeaders.IfNoneMatch != null)
                {
                    throw new WebDavException(WebDavStatusCode.PreconditionFailed);
                }

                throw new WebDavException(WebDavStatusCode.NotFound);
            }

            await _context.RequestHeaders
                .ValidateAsync(selectionResult.TargetEntry, cancellationToken).ConfigureAwait(false);

            List<IEntry> entries = new();
            if (selectionResult.ResultType == SelectionResultType.FoundDocument)
            {
                entries.Add(selectionResult.Document);
            }
            else
            {
                Debug.Assert(selectionResult.Collection != null, "selectionResult.Collection != null");
                Debug.Assert(selectionResult.ResultType == SelectionResultType.FoundCollection, "selectionResult.ResultType == SelectionResultType.FoundCollection");
                entries.Add(selectionResult.Collection);
                IRecusiveChildrenCollector collector = selectionResult.Collection as IRecusiveChildrenCollector;
                DepthHeader depth = _context.RequestHeaders.Depth ?? (collector == null ? DepthHeader.One : DepthHeader.Infinity);
                if (depth == DepthHeader.One)
                {
                    entries.AddRange(await selectionResult.Collection.GetChildrenAsync(cancellationToken).ConfigureAwait(false));
                }
                else if (depth == DepthHeader.Infinity)
                {
                    if (collector == null)
                    {
                        // Cannot recursively collect the children with infinite depth
                        return new WebDavResult<error>(WebDavStatusCode.Forbidden, new error()
                        {
                            ItemsElementName = new[] { ItemsChoiceType.propfindfinitedepth, },
                            Items = new[] { new object(), },
                        });
                    }

                    int remainingDepth = depth.OrderValue - (depth != DepthHeader.Infinity ? 1 : 0);
                    await using IAsyncEnumerator<IEntry> entriesEnumerator = collector.GetEntries(remainingDepth).GetAsyncEnumerator(cancellationToken);
                    while (await entriesEnumerator.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                    {
                        entries.Add(entriesEnumerator.Current);
                    }
                }
            }

            if (request == null)
            {
                return await HandleAllPropAsync(entries, cancellationToken).ConfigureAwait(false);
            }

            Debug.Assert(request.ItemsElementName != null, "request.ItemsElementName != null");
            switch (request.ItemsElementName[0])
            {
                case ItemsChoiceType1.allprop:
                    return await HandleAllPropAsync(request, entries, cancellationToken).ConfigureAwait(false);
                case ItemsChoiceType1.prop:
                    Debug.Assert(request.Items != null, "request.Items != null");
                    Debug.Assert(request.Items[0] != null, "request.Items[0] != null");
                    return await HandlePropAsync((prop)request.Items[0], entries, cancellationToken).ConfigureAwait(false);
                case ItemsChoiceType1.propname:
                    return await HandlePropNameAsync(entries, cancellationToken).ConfigureAwait(false);
            }

            throw new WebDavException(WebDavStatusCode.Forbidden);
        }



        private async Task<IWebDavResult> HandlePropAsync(prop prop, IReadOnlyCollection<IEntry> entries, CancellationToken cancellationToken)
        {
            List<response> responses = new();
            foreach (IEntry entry in entries)
            {
                string entryPath = entry.Path.OriginalString;
                Uri href = _context.PublicControllerUrl.Append(entryPath, true);
                if (!_options.UseAbsoluteHref)
                {
                    href = new Uri("/" + _context.PublicRootUrl.MakeRelativeUri(href).OriginalString, UriKind.Relative);
                }

                PropertyCollector collector = new(this, _context, new ReadableFilter(), new PropFilter(prop));
                IReadOnlyCollection<propstat> propStats = await collector.GetPropertiesAsync(entry, int.MaxValue, cancellationToken).ConfigureAwait(false);

                response response = new()
                {
                    href = href.OriginalString,
                    ItemsElementName = propStats.Select(x => ItemsChoiceType2.propstat).ToArray(),
                    Items = propStats.Cast<object>().ToArray(),
                };

                responses.Add(response);
            }

            multistatus result = new()
            {
                response = responses.ToArray(),
            };

            return new WebDavResult<multistatus>(WebDavStatusCode.MultiStatus, result);
        }



        private Task<IWebDavResult> HandleAllPropAsync(propfind request, IEnumerable<IEntry> entries, CancellationToken cancellationToken)
        {
            include include = request.ItemsElementName.Select((x, i) => Tuple.Create(x, i)).Where(x => x.Item1 == ItemsChoiceType1.include).Select(x => (include)request.Items[x.Item2]).FirstOrDefault();
            return HandleAllPropAsync(include, entries, cancellationToken);
        }



        private Task<IWebDavResult> HandleAllPropAsync(IEnumerable<IEntry> entries, CancellationToken cancellationToken)
        {
            return HandleAllPropAsync((include)null, entries, cancellationToken);
        }

        // ReSharper disable once UnusedParameter.Local


        private async Task<IWebDavResult> HandleAllPropAsync(include include, IEnumerable<IEntry> entries, CancellationToken cancellationToken)
        {
            List<response> responses = new();
            foreach (IEntry entry in entries)
            {
                string entryPath = entry.Path.OriginalString;
                Uri href = _context.PublicControllerUrl.Append(entryPath, true);
                if (!_options.UseAbsoluteHref)
                {
                    href = new Uri("/" + _context.PublicRootUrl.MakeRelativeUri(href).OriginalString, UriKind.Relative);
                }

                PropertyCollector collector = new(this, _context, new ReadableFilter(), new CostFilter(0));
                IReadOnlyCollection<propstat> propStats = await collector.GetPropertiesAsync(entry, 0, cancellationToken).ConfigureAwait(false);

                response response = new()
                {
                    href = href.OriginalString,
                    ItemsElementName = propStats.Select(x => ItemsChoiceType2.propstat).ToArray(),
                    Items = propStats.Cast<object>().ToArray(),
                };

                responses.Add(response);
            }

            multistatus result = new()
            {
                response = responses.ToArray(),
            };

            return new WebDavResult<multistatus>(WebDavStatusCode.MultiStatus, result);
        }



        private async Task<IWebDavResult> HandlePropNameAsync(IEnumerable<IEntry> entries, CancellationToken cancellationToken)
        {
            List<response> responses = new();
            foreach (IEntry entry in entries)
            {
                string entryPath = entry.Path.OriginalString;
                Uri href = _context.PublicControllerUrl.Append(entryPath, true);
                if (!_options.UseAbsoluteHref)
                {
                    href = new Uri("/" + _context.PublicRootUrl.MakeRelativeUri(href).OriginalString, UriKind.Relative);
                }

                PropertyCollector collector = new(this, _context, new ReadableFilter(), new CostFilter(0));
                IReadOnlyCollection<propstat> propStats = await collector.GetPropertyNamesAsync(entry, cancellationToken).ConfigureAwait(false);

                response response = new()
                {
                    href = href.OriginalString,
                    ItemsElementName = propStats.Select(x => ItemsChoiceType2.propstat).ToArray(),
                    Items = propStats.Cast<object>().ToArray(),
                };

                responses.Add(response);
            }

            multistatus result = new()
            {
                response = responses.ToArray(),
            };

            return new WebDavResult<multistatus>(WebDavStatusCode.MultiStatus, result);
        }

        private class PropertyCollector
        {

            private readonly PropFindHandler _handler;


            private readonly IWebDavContext _host;



            private readonly IPropertyFilter[] _filters;

            public PropertyCollector(PropFindHandler handler, IWebDavContext host, params IPropertyFilter[] filters)
            {
                _handler = handler;
                _host = host;
                _filters = filters;
            }

            public async Task<IReadOnlyCollection<propstat>> GetPropertiesAsync(IEntry entry, int maxCost, CancellationToken cancellationToken)
            {
                foreach (IPropertyFilter filter in _filters)
                {
                    filter.Reset();
                }

                List<XElement> propElements = new();
                await using (IAsyncEnumerator<Props.IUntypedReadableProperty> propsEnumerator = entry.GetProperties(_host.Dispatcher, maxCost).GetAsyncEnumerator(cancellationToken))
                {
                    while (await propsEnumerator.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                    {
                        Props.IUntypedReadableProperty property = propsEnumerator.Current;

                        if (!_filters.All(x => x.IsAllowed(property)))
                        {
                            continue;
                        }

                        foreach (IPropertyFilter filter in _filters)
                        {
                            filter.NotifyOfSelection(property);
                        }

                        XElement element;
                        element = property is LockDiscoveryProperty lockDiscoveryProp
                            ? await lockDiscoveryProp.GetXmlValueAsync(
                                _handler._options.OmitLockOwner,
                                _handler._options.OmitLockToken,
                                cancellationToken).ConfigureAwait(false)
                            : await property.GetXmlValueAsync(cancellationToken).ConfigureAwait(false);

                        propElements.Add(element);
                    }
                }

                List<propstat> result = new();
                if (propElements.Count != 0)
                {
                    result.Add(
                        new propstat()
                        {
                            prop = new prop()
                            {
                                Any = propElements.ToArray(),
                            },
                            status = new Status(_host.RequestProtocol, WebDavStatusCode.OK).ToString(),
                        });
                }

                Dictionary<WebDavStatusCode, List<XName>> missingProperties = _filters
                    .SelectMany(x => x.GetMissingProperties())
                    .GroupBy(x => x.StatusCode, x => x.Key)
                    .ToDictionary(x => x.Key, x => x.Distinct().ToList());
                foreach (KeyValuePair<WebDavStatusCode, List<XName>> item in missingProperties)
                {
                    result.Add(
                        new propstat()
                        {
                            prop = new prop()
                            {
                                Any = item.Value.Select(x => new XElement(x)).ToArray(),
                            },
                            status = new Status(_host.RequestProtocol, item.Key).ToString(),
                        });
                }

                return result;
            }



            public async Task<IReadOnlyCollection<propstat>> GetPropertyNamesAsync(IEntry entry, CancellationToken cancellationToken)
            {
                foreach (IPropertyFilter filter in _filters)
                {
                    filter.Reset();
                }

                List<XElement> propElements = new();
                await using (IAsyncEnumerator<Props.IUntypedReadableProperty> propsEnumerator = entry.GetProperties(_host.Dispatcher).GetAsyncEnumerator(cancellationToken))
                {
                    while (await propsEnumerator.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                    {
                        Props.IUntypedReadableProperty property = propsEnumerator.Current;

                        if (!_filters.All(x => x.IsAllowed(property)))
                        {
                            continue;
                        }

                        foreach (IPropertyFilter filter in _filters)
                        {
                            filter.NotifyOfSelection(property);
                        }

                        Props.IUntypedReadableProperty readableProp = property;
                        XElement element = new(readableProp.Name);
                        propElements.Add(element);
                    }
                }

                List<propstat> result = new();
                if (propElements.Count != 0)
                {
                    result.Add(
                        new propstat()
                        {
                            prop = new prop()
                            {
                                Any = propElements.ToArray(),
                            },
                            status = new Status(_host.RequestProtocol, WebDavStatusCode.OK).ToString(),
                        });
                }

                Dictionary<WebDavStatusCode, List<XName>> missingProperties = _filters
                    .SelectMany(x => x.GetMissingProperties())
                    .GroupBy(x => x.StatusCode, x => x.Key)
                    .ToDictionary(x => x.Key, x => x.Distinct().ToList());
                foreach (KeyValuePair<WebDavStatusCode, List<XName>> item in missingProperties)
                {
                    result.Add(
                        new propstat()
                        {
                            prop = new prop()
                            {
                                Any = item.Value.Select(x => new XElement(x)).ToArray(),
                            },
                            status = new Status(_host.RequestProtocol, item.Key).ToString(),
                        });
                }

                return result;
            }
        }
    }
}
