// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Net;
using System.Net.Http;

namespace System.Xml
{
    internal sealed partial class XmlDownloadManager
    {
        internal Stream GetStream(Uri uri, ICredentials? credentials, IWebProxy? proxy)
        {
            if (uri.Scheme == "file")
            {
                return new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            }
            else
            {
                return GetNonFileStream(uri, credentials, proxy);
            }
        }

        private Stream GetNonFileStream(Uri uri, ICredentials? credentials, IWebProxy? proxy)
        {
            var handler = new HttpClientHandler();
            using (var client = new HttpClient(handler))
            {
#pragma warning disable CA1416 // Validate platform compatibility, 'credentials' and 'proxy' will not be set for browser, so safe to suppress
                if (credentials != null)
                {
                    handler.Credentials = credentials;
                }
                if (proxy != null)
                {
                    handler.Proxy = proxy;
                }
#pragma warning restore CA1416

                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using (Stream respStream = client.Send(request).Content.ReadAsStream())
                {
                    var result = new MemoryStream();
                    respStream.CopyTo(result);
                    result.Position = 0;
                    return result;
                }
            }
        }
    }
}
