// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Configuration;
using NuGet.Protocol.Core.Types;

namespace NuGet.Protocol
{
    public class HttpHandlerResourceV3Provider : ResourceProvider
    {
        public HttpHandlerResourceV3Provider()
            : base(typeof(HttpHandlerResource),
                  nameof(HttpHandlerResourceV3Provider),
                  NuGetResourceProviderPositions.Last)
        {
        }

        public override Task<Tuple<bool, INuGetResource>> TryCreate(SourceRepository source, CancellationToken token)
        {
            Debug.Assert(source.PackageSource.IsHttp, "HTTP handler requested for a non-http source.");

            HttpHandlerResourceV3 curResource = null;

            if (source.PackageSource.IsHttp)
            {
                curResource = CreateResource(source.PackageSource);
            }

            return Task.FromResult(new Tuple<bool, INuGetResource>(curResource != null, curResource));
        }

        private static HttpHandlerResourceV3 CreateResource(PackageSource packageSource)
        {
            var sourceUri = packageSource.SourceUri;
            var proxy = ProxyCache.Instance.GetProxy(sourceUri);
            var useProxy = ProxyCache.Instance.UseProxy();
#if IS_CORECLR
            var defaultProxyCredentials = ProxyCache.Instance.GetDefaultProxyCredentials();
#endif
            // replace the handler with the proxy aware handler
            var clientHandler = new HttpClientHandler
            {
                UseProxy = useProxy,
                Proxy = proxy,
#if IS_CORECLR
                DefaultProxyCredentials = defaultProxyCredentials,
#endif
                AutomaticDecompression = (DecompressionMethods.GZip | DecompressionMethods.Deflate)
            };

            
            // HTTP handler pipeline can be injected here, around the client handler
            HttpMessageHandler messageHandler = clientHandler;

            if (proxy != null)
            {
                messageHandler = new ProxyAuthenticationHandler(clientHandler, HttpHandlerResourceV3.CredentialService, ProxyCache.Instance);
            }

#if !IS_CORECLR
            {
                var innerHandler = messageHandler;

                messageHandler = new StsAuthenticationHandler(packageSource, TokenStore.Instance)
                {
                    InnerHandler = messageHandler
                };
            }
#endif
            {
                var innerHandler = messageHandler;

                messageHandler = new HttpSourceAuthenticationHandler(packageSource, clientHandler, HttpHandlerResourceV3.CredentialService)
                {
                    InnerHandler = innerHandler
                };
            }

            var resource = new HttpHandlerResourceV3(clientHandler, messageHandler);

            return resource;
        }
    }
}