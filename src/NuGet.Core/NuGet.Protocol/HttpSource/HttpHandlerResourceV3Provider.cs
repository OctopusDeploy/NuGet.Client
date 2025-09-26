// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Configuration;
using NuGet.Protocol.Core.Types;

#if NETSTANDARD2_0
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
#endif

namespace NuGet.Protocol
{
    /// <summary>
    /// Provides an HttpHandlerResourceV3 for a given package source.
    /// </summary>
    /// <remarks>
    /// HttpHandlerResourceV3Provider
    ///     ↓ (provides HttpHandlerResource)
    /// HttpSourceResourceProvider
    ///     ↓ (uses HttpHandlerResource to create HttpSource)
    /// HttpSourceResource
    ///     ↓ (used by all HTTP-based providers)
    /// All V3 and V2 Resources that need HTTP access
    /// </remarks>
    public class HttpHandlerResourceV3Provider : ResourceProvider
    {
        private readonly IProxyCache _proxyCache;

#if NETSTANDARD2_0
        internal static Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> DangerousAcceptAnyServerCertificateValidator =
            (message, certificate, chain, policyErrors) => true;
#endif

        public HttpHandlerResourceV3Provider()
            : this(ProxyCache.Instance)
        {
        }

        internal HttpHandlerResourceV3Provider(IProxyCache proxyCache)
            : base(typeof(HttpHandlerResource),
                  nameof(HttpHandlerResourceV3Provider),
                  NuGetResourceProviderPositions.Last)
        {
            _proxyCache = proxyCache ?? throw new ArgumentNullException(nameof(proxyCache));
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

        // To save time of someone coming back to this code later, here is a list of resources that use HttpHandlerResourceV3Provider:
        //
        // All V3 Resources that depend on HttpSourceResource - INDIRECT USERS
        // Based on the grep results, these all call await source.GetResourceAsync<HttpSourceResource>(token):
        // PackageUpdateResource (via PackageUpdateResourceV3Provider)
        // RegistrationResourceV3 (via RegistrationResourceV3Provider)
        // ServiceIndexResourceV3 (via ServiceIndexResourceV3Provider)
        // PackageSearchResource (via PackageSearchResourceV3Provider)
        // PackageMetadataResource (via PackageMetadataResourceV3Provider)
        // DownloadResource (via DownloadResourceV3Provider)
        // DependencyInfoResource (via DependencyInfoResourceV3Provider)
        // AutoCompleteResource (via AutoCompleteResourceV3Provider)
        //
        // All V2 Resources that depend on HttpSourceResource - INDIRECT USERS
        // ODataServiceDocumentResourceV2 (via ODataServiceDocumentResourceV2Provider)
        // Plus all V2 feed providers (search, metadata, download, etc.)
        private HttpHandlerResourceV3 CreateResource(PackageSource packageSource)
        {
            var sourceUri = packageSource.SourceUri;
            var proxy = _proxyCache.GetProxy(sourceUri);
#if IS_CORECLR
            var defaultProxyCredentials = ProxyCache.Instance.GetDefaultProxyCredentials();
#endif

            // replace the handler with the proxy aware handler
            var clientHandler = new HttpClientHandler
            {
                Proxy = proxy,
#if IS_CORECLR
                DefaultProxyCredentials = defaultProxyCredentials,
#endif
                AutomaticDecompression = (DecompressionMethods.GZip | DecompressionMethods.Deflate),
            };

#if NETSTANDARD2_0
            if (packageSource.DisableTLSCertificateValidation)
            {
                clientHandler.ServerCertificateCustomValidationCallback = DangerousAcceptAnyServerCertificateValidator;
            }
#else
            if (packageSource.DisableTLSCertificateValidation)
            {
                clientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
#endif

#if IS_DESKTOP
            if (packageSource.MaxHttpRequestsPerSource > 0)
            {
                clientHandler.MaxConnectionsPerServer = packageSource.MaxHttpRequestsPerSource;
            }
#endif

            // Setup http client handler client certificates
            if (packageSource.ClientCertificates != null)
            {
                clientHandler.ClientCertificates.AddRange(packageSource.ClientCertificates.ToArray());
            }

            // HTTP handler pipeline can be injected here, around the client handler
            HttpMessageHandler messageHandler = new ServerWarningLogHandler(clientHandler);

            if (proxy != null)
            {
                var innerHandler = messageHandler;

                messageHandler = new ProxyAuthenticationHandler(clientHandler, HttpHandlerResourceV3.CredentialService?.Value, ProxyCache.Instance)
                {
                    InnerHandler = innerHandler
                };
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

                messageHandler = new HttpSourceAuthenticationHandler(packageSource, clientHandler, HttpHandlerResourceV3.CredentialService?.Value)
                {
                    InnerHandler = innerHandler
                };
            }

            var resource = new HttpHandlerResourceV3(clientHandler, messageHandler);

            return resource;
        }
    }
}
