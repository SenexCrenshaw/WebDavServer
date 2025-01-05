// <copyright file="EntryExtensions.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Props.Dead;



namespace FubarDev.WebDavServer.Tests.Support
{
    public static class EntryExtensions
    {
        public static Task<IReadOnlyCollection<XElement>> GetPropertyElementsAsync(
             this IEntry entry,
             IWebDavDispatcher dispatcher,
            CancellationToken ct)
        {
            return GetPropertyElementsAsync(entry, dispatcher, false, ct);
        }

        public static async Task<IReadOnlyCollection<XElement>> GetPropertyElementsAsync(
             this IEntry entry,
             IWebDavDispatcher dispatcher,
            bool skipEtag,
            CancellationToken ct)
        {
            List<XElement> result = [];
            await using (IAsyncEnumerator<Props.IUntypedReadableProperty> propEnum = entry.GetProperties(dispatcher).GetAsyncEnumerator(ct))
            {
                while (await propEnum.MoveNextAsync(ct).ConfigureAwait(false))
                {
                    Props.IUntypedReadableProperty prop = propEnum.Current;
                    if (skipEtag && prop.Name == GetETagProperty.PropertyName)
                    {
                        continue;
                    }

                    XElement element = await prop.GetXmlValueAsync(ct).ConfigureAwait(false);
                    result.Add(element);
                }
            }

            return result;
        }
    }
}
