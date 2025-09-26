// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using NuGet.Common;

namespace NuGet.Configuration
{
    [SuppressMessage("Style", "IDE1006:Naming Styles")]
    public class ProxyCache : IProxyCache, IProxyCredentialCache
    {
#if !IS_CORECLR
        /// <summary>
        /// Capture the default System Proxy so that it can be re-used by the IProxyFinder
        /// because we can't rely on WebRequest.DefaultWebProxy since someone can modify the DefaultWebProxy
        /// property and we can't tell if it was modified and if we are still using System Proxy Settings or not.
        /// One limitation of this method is that it does not look at the config file to get the defined proxy
        /// settings.
        /// </summary>
        private static readonly IWebProxy _originalSystemProxy = WebRequest.GetSystemWebProxy();
#endif
        /// <summary>
        /// Octopus overrides the (user or system proxy) with a custom proxy implementation
        /// </summary>
        /// <remarks>
        /// This was a fix implemented on an older version of NuGet 3.6.1
        /// https://github.com/OctopusDeploy/Issues/issues/2959
        /// This fix was rejected https://github.com/NuGet/NuGet.Client/pull/1153
        /// That lead us to fork and implement this fix.
        ///
        /// In updating Octopus to 6.14.x we opted to maintain this fix as a work around.
        /// </remarks>
        private IWebProxy? _overrideProxyWithCustomProxy;
        private ICredentials? _overrideDefaultProxyCredentials;

        private readonly ConcurrentDictionary<Uri, ICredentials> _cachedCredentials = new ConcurrentDictionary<Uri, ICredentials>();

        private readonly ISettings _settings;
        private readonly IEnvironmentVariableReader _environment;

        // It's not likely that http proxy settings are set in machine wide settings,
        // so not passing machine wide settings to Settings.LoadDefaultSettings() should be fine.
        private static readonly Lazy<ProxyCache> _instance = new Lazy<ProxyCache>(() => FromDefaultSettings());

        private static ProxyCache FromDefaultSettings()
        {
            return new ProxyCache(
                Settings.LoadDefaultSettings(root: null, configFileName: null, machineWideSettings: null),
                EnvironmentVariableWrapper.Instance);
        }

        public static ProxyCache Instance => _instance.Value;

        public Guid Version { get; private set; } = Guid.NewGuid();

        public ProxyCache(ISettings settings, IEnvironmentVariableReader environment)
        {
            _settings = settings;
            _environment = environment;
        }

        /// <summary>
        /// Returns a proxy (if it exists) based on what is set in priority order of:
        /// Custom proxy, User configured proxy, System proxy, no proxy.
        /// </summary>
        /// <param name="sourceUri"></param>
        /// <returns></returns>
        public IWebProxy? GetProxy(Uri sourceUri)
        {
            // Use the override proxy if someone has set it in code
            if (_overrideProxyWithCustomProxy is not null)
            {
                return _overrideProxyWithCustomProxy;
            }

            // Original NuGet Code below:
            // Check if the user has configured proxy details in settings or in the environment.
            var configuredProxy = GetUserConfiguredProxy();
            if (configuredProxy != null)
            {
                TryAddProxyCredentialsToCache(configuredProxy);
                configuredProxy.Credentials = this;
                return configuredProxy;
            }

#if !IS_CORECLR
            if (IsSystemProxySet(sourceUri))
            {
                var systemProxy = GetSystemProxy(sourceUri);
                TryAddProxyCredentialsToCache(systemProxy);
                systemProxy.Credentials = this;
                return systemProxy;
            }
#endif
            return null;
        }

        /// <summary>
        /// Get the manually overwritten DefaultProxyCredentials.
        /// for when we don't set the proxy, but we want the default proxy credentials.
        /// </summary>
        /// <returns></returns>
        public ICredentials? GetDefaultProxyCredentials()
        {
            return _overrideDefaultProxyCredentials;
        }

        // Adds new proxy credentials to cache if there's not any in there yet
        private bool TryAddProxyCredentialsToCache(WebProxy configuredProxy)
        {
            // If a proxy was cached, it means the stored credentials are incorrect. Use the cached one in this case.
            var proxyCredentials = configuredProxy.Credentials ?? CredentialCache.DefaultCredentials;
            return _cachedCredentials.TryAdd(configuredProxy.ProxyAddress, proxyCredentials);
        }

        /// <summary>
        /// Override the proxy and default proxy credentials settings
        /// </summary>
        /// <example>
        /// <code>
        /// var proxy = new WebProxy("http://proxy.com");
        /// var credentials = new NetworkCredential("username", "password");
        /// ProxyCache.Instance.SetOverrideProxySettings(proxy, credentials);
        /// </code>
        /// </example>
        /// <param name="proxy">Optional, if not set, default to cache behaviour, user -> system -> no proxy</param>
        /// <param name="defaultProxyCredentials">This sets the "defaultProxyCredentials" on the HttpClientHandler (its default is null)</param>
        public void SetOverrideProxySettings(IWebProxy? proxy, ICredentials? defaultProxyCredentials)
        {
            _overrideProxyWithCustomProxy = proxy;
            _overrideDefaultProxyCredentials = defaultProxyCredentials;
        }

        public WebProxy? GetUserConfiguredProxy()
        {
            // Try reading from the settings. The values are stored as 3 config values http_proxy, http_proxy.user, http_proxy.password
            var host = SettingsUtility.GetConfigValue(_settings, ConfigurationConstants.HostKey);
            if (!string.IsNullOrEmpty(host))
            {
                // The host is the minimal value we need to assume a user configured proxy.
                var webProxy = new WebProxy(host!);

                if (RuntimeEnvironmentHelper.IsWindows)
                {
                    var userName = SettingsUtility.GetConfigValue(_settings, ConfigurationConstants.UserKey);
                    var password = SettingsUtility.GetDecryptedValueForAddItem(_settings, ConfigurationConstants.Config, ConfigurationConstants.PasswordKey);

                    if (!string.IsNullOrEmpty(userName)
                        && !string.IsNullOrEmpty(password))
                    {
                        webProxy.Credentials = new NetworkCredential(userName, password);
                    }
                }
                var noProxy = SettingsUtility.GetConfigValue(_settings, ConfigurationConstants.NoProxy);
                if (!string.IsNullOrEmpty(noProxy))
                {
                    // split comma-separated list of domains
                    webProxy.BypassList = noProxy!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                }

                return webProxy;
            }

            // Next try reading from the environment variable http_proxy. This would be specified as http://<username>:<password>@proxy.com
            host = _environment.GetEnvironmentVariable(ConfigurationConstants.HostKey);
            Uri? uri;
            if (!string.IsNullOrEmpty(host)
                && Uri.TryCreate(host, UriKind.Absolute, out uri))
            {
                var webProxy = new WebProxy(uri.GetComponents(
                    UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped));
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var credentials = uri.UserInfo.Split(':');
                    if (credentials.Length > 1)
                    {
                        webProxy.Credentials = new NetworkCredential(
                            userName: credentials[0], password: credentials[1]);
                    }
                }

                var noProxy = _environment.GetEnvironmentVariable(ConfigurationConstants.NoProxy);
                if (!string.IsNullOrEmpty(noProxy))
                {
                    // split comma-separated list of domains
                    webProxy.BypassList = noProxy!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                }

                return webProxy;
            }
            return null;
        }

        public void UpdateCredential(Uri proxyAddress, NetworkCredential credentials)
        {
            if (credentials == null)
            {
                throw new ArgumentNullException(nameof(credentials));
            }

            _cachedCredentials.AddOrUpdate(
                proxyAddress,
                addValueFactory: _ => { Version = Guid.NewGuid(); return credentials; },
                updateValueFactory: (_, __) => { Version = Guid.NewGuid(); return credentials; });
        }

        public NetworkCredential? GetCredential(Uri proxyAddress, string authType)
        {
            ICredentials? cachedCredentials;
            if (_cachedCredentials.TryGetValue(proxyAddress, out cachedCredentials))
            {
                return cachedCredentials.GetCredential(proxyAddress, authType);
            }

            return null;
        }

        [Obsolete("Retained for backcompat only. Use UpdateCredential instead")]
        public void Add(IWebProxy? proxy)
        {
            var webProxy = proxy as WebProxy;
            if (webProxy?.Credentials is not null)
            {
                _cachedCredentials.TryAdd(webProxy.ProxyAddress, webProxy.Credentials);
            }
        }

#if !IS_CORECLR
        private static WebProxy GetSystemProxy(Uri uri)
        {
            // WebRequest.DefaultWebProxy seems to be more capable in terms of getting the default
            // proxy settings instead of the WebRequest.GetSystemProxy()
            var proxyUri = _originalSystemProxy.GetProxy(uri);
            return new WebProxy(proxyUri);
        }

        /// <summary>
        /// Return true or false if connecting through a proxy server
        /// </summary>
        /// <param name="uri"></param>
        /// <returns></returns>
        private static bool IsSystemProxySet(Uri uri)
        {
            // The reason for not calling the GetSystemProxy is because the object
            // that will be returned is no longer going to be the proxy that is set by the settings
            // on the users machine only the Address is going to be the same.
            // Not sure why the .NET team did not want to expose all of the useful settings like
            // ByPass list and other settings that we can't get because of it.
            // Anyway the reason why we need the DefaultWebProxy is to see if the uri that we are
            // getting the proxy for to should be bypassed or not. If it should be bypassed then
            // return that we don't need a proxy and we should try to connect directly.
            var proxy = WebRequest.DefaultWebProxy;
            if (proxy != null)
            {
                var proxyUri = proxy.GetProxy(uri);
                if (proxyUri != null)
                {
                    var proxyAddress = new Uri(proxyUri.AbsoluteUri);
                    if (string.Equals(proxyAddress.AbsoluteUri, uri.AbsoluteUri, PathUtility.GetStringComparisonBasedOnOS()))
                    {
                        return false;
                    }
                    return !proxy.IsBypassed(uri);
                }
            }

            return false;
        }
#endif
    }
}
