// <copyright file="WebDavFullDocumentResult.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Model;
using FubarDev.WebDavServer.Props;
using FubarDev.WebDavServer.Props.Dead;
using FubarDev.WebDavServer.Props.Live;
using FubarDev.WebDavServer.Utils;



namespace FubarDev.WebDavServer.Handlers.Impl.GetResults
{
    internal class WebDavFullDocumentResult : WebDavResult
    {

        private readonly IDocument _document;

        private readonly bool _returnFile;

        public WebDavFullDocumentResult(IDocument document, bool returnFile)
            : base(WebDavStatusCode.OK)
        {
            _document = document;
            _returnFile = returnFile;
        }

        public override async Task ExecuteResultAsync(IWebDavResponse response, CancellationToken ct)
        {
            await base.ExecuteResultAsync(response, ct).ConfigureAwait(false);

            if (_document.FileSystem.SupportsRangedRead)
            {
                response.Headers["Accept-Ranges"] = new[] { "bytes" };
            }

            List<IUntypedReadableProperty> properties = await _document.GetProperties(response.Dispatcher).ToListAsync(ct).ConfigureAwait(false);
            GetETagProperty etagProperty = properties.OfType<GetETagProperty>().FirstOrDefault();
            if (etagProperty != null)
            {
                Model.Headers.EntityTag propValue = await etagProperty.GetValueAsync(ct).ConfigureAwait(false);
                response.Headers["ETag"] = new[] { propValue.ToString() };
            }

            if (!_returnFile)
            {
                LastModifiedProperty lastModifiedProp = properties.OfType<LastModifiedProperty>().FirstOrDefault();
                if (lastModifiedProp != null)
                {
                    DateTime propValue = await lastModifiedProp.GetValueAsync(ct).ConfigureAwait(false);
                    response.Headers["Last-Modified"] = new[] { propValue.ToString("R") };
                }

                return;
            }

            using System.IO.Stream stream = await _document.OpenReadAsync(ct).ConfigureAwait(false);
            using StreamContent content = new(stream);
            // I'm storing the headers in the content, because I'm too lazy to
            // look up the header names and the formatting of its values.
            await SetPropertiesToContentHeaderAsync(content, properties, ct).ConfigureAwait(false);

            foreach (KeyValuePair<string, IEnumerable<string>> header in content.Headers)
            {
                response.Headers.Add(header.Key, header.Value.ToArray());
            }

            // Use the CopyToAsync function of the stream itself, because
            // we're able to pass the cancellation token. This is a workaround
            // for issue dotnet/corefx#9071 and fixes FubarDevelopment/WebDavServer#47.
            await stream.CopyToAsync(response.Body, 81920, ct)
                .ConfigureAwait(false);
        }

        private async Task SetPropertiesToContentHeaderAsync(HttpContent content, IReadOnlyCollection<IUntypedReadableProperty> properties, CancellationToken ct)
        {
            LastModifiedProperty lastModifiedProp = properties.OfType<LastModifiedProperty>().FirstOrDefault();
            if (lastModifiedProp != null)
            {
                DateTime propValue = await lastModifiedProp.GetValueAsync(ct).ConfigureAwait(false);
                content.Headers.LastModified = new DateTimeOffset(propValue);
            }

            GetContentLanguageProperty contentLanguageProp = properties.OfType<GetContentLanguageProperty>().FirstOrDefault();
            if (contentLanguageProp != null)
            {
                (bool, string) propValue = await contentLanguageProp.TryGetValueAsync(ct).ConfigureAwait(false);
                if (propValue.Item1)
                {
                    content.Headers.ContentLanguage.Add(propValue.Item2);
                }
            }

            string contentType;
            GetContentTypeProperty contentTypeProp = properties.OfType<GetContentTypeProperty>().FirstOrDefault();
            contentType = contentTypeProp != null ? await contentTypeProp.GetValueAsync(ct).ConfigureAwait(false) : MimeTypesMap.DefaultMimeType;

            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

            ContentDispositionHeaderValue contentDisposition = new("attachment")
            {
                FileName = _document.Name,
                FileNameStar = _document.Name,
            };

            content.Headers.ContentDisposition = contentDisposition;
        }
    }
}
