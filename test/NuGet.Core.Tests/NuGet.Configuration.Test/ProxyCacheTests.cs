// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Net;
using Moq;
using NuGet.Common;
using NuGet.Test.Utility;
using Xunit;

namespace NuGet.Configuration.Test
{
    public class ProxyCacheTests
    {
        [Fact]
        public void GetUserConfiguredProxy_IfValueIsNotFoundInEnvironmentOrSettings_ReturnsNull()
        {
            // Arrange
            var settings = Mock.Of<ISettings>();
            var environment = Mock.Of<IEnvironmentVariableReader>();
            var proxyCache = new ProxyCache(settings, environment);

            // Act
            var proxy = proxyCache.GetUserConfiguredProxy();

            // Assert
            Assert.Null(proxy);
        }

        [Fact]
        public void GetUserConfiguredProxy_IgnoresNullOrEmptyHostValuesInSetting()
        {
            // Arrange
            var settings = new Mock<ISettings>(MockBehavior.Strict);
            settings.Setup(s => s.GetSection("config"))
                .Returns(() => null);
            var environment = Mock.Of<IEnvironmentVariableReader>();
            var proxyCache = new ProxyCache(settings.Object, environment);

            // Act
            var proxy = proxyCache.GetUserConfiguredProxy();

            // Assert
            Assert.Null(proxy);
        }

        [PlatformFact(Platform.Windows)]
        public void GetUserConfiguredProxy_OnWindows_ReadsCredentialsFromSettings()
        {
            // Arrange
            var host = "http://127.0.0.1";
            var user = "username";
            var encryptedPassword = EncryptionUtility.EncryptString("password");
            var settings = new Mock<ISettings>(MockBehavior.Strict);
            settings.Setup(s => s.GetSection("config"))
                .Returns(new VirtualSettingSection("config",
                    new AddItem("http_proxy", host),
                    new AddItem("http_proxy.user", user),
                    new AddItem("http_proxy.password", encryptedPassword)));

            var environment = Mock.Of<IEnvironmentVariableReader>();
            var proxyCache = new ProxyCache(settings.Object, environment);

            // Act
            var proxy = proxyCache.GetUserConfiguredProxy() as WebProxy;

            // Assert
            AssertProxy(host, user, "password", proxy);
        }

        [Fact]
        public void GetUserConfiguredProxy_IfNullOrEmptyInSettings_DoesNotSetProxyCredentials()
        {
            // Arrange
            var host = "http://127.0.0.1";
            var settings = new Mock<ISettings>(MockBehavior.Strict);
            settings.Setup(s => s.GetSection("config"))
                .Returns(new VirtualSettingSection("config",
                    new AddItem("http_proxy", host)));

            var environment = Mock.Of<IEnvironmentVariableReader>();
            var proxyCache = new ProxyCache(settings.Object, environment);

            // Act
            var proxy = proxyCache.GetUserConfiguredProxy() as WebProxy;

            // Assert
            AssertProxy(host, null, null, proxy);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("random-junk-value")]
        public void GetUserConfiguredProxy_IfNotValid_IgnoresEnvironmentVariable(string proxyValue)
        {
            // Arrange
            var settings = Mock.Of<ISettings>();
            var environment = new Mock<IEnvironmentVariableReader>(MockBehavior.Strict);
            environment.Setup(s => s.GetEnvironmentVariable("http_proxy")).Returns(proxyValue);

            var proxyCache = new ProxyCache(settings, environment.Object);

            // Act
            var proxy = proxyCache.GetUserConfiguredProxy();

            // Assert
            Assert.Null(proxy);
        }

        [Fact]
        public void GetUserConfiguredProxy_ReadsHostFromEnvironmentVariable()
        {
            // Arrange
            var settings = Mock.Of<ISettings>();
            var environment = new Mock<IEnvironmentVariableReader>(MockBehavior.Strict);
            environment.Setup(s => s.GetEnvironmentVariable("http_proxy")).Returns("http://localhost:8081");
            environment.Setup(s => s.GetEnvironmentVariable("no_proxy")).Returns("");

            var proxyCache = new ProxyCache(settings, environment.Object);

            // Act
            var proxy = proxyCache.GetUserConfiguredProxy() as WebProxy;

            // Assert
            AssertProxy("http://localhost:8081/", null, null, proxy);
        }

        [Theory]
        [InlineData("http://username:password@localhost:8081/proxy.dat", "http://localhost:8081/proxy.dat", "username", "password", new string[] { ".*.com" })]
        [InlineData("http://username:password@localhost:8081/proxy.dat", "http://localhost:8081/proxy.dat", "username", "password", new string[] { })]
        [InlineData("http://localhost:8081/proxy/.conf", "http://localhost:8081/proxy/.conf", null, null, new string[] { ".*.com", ".*.org" })]
        [InlineData("http://localhost:8081/proxy/.conf", "http://localhost:8081/proxy/.conf", null, null, new string[] { })]
        public void GetUserConfiguredProxy_ReadsCredentialsFromEnvironmentVariable(string input, string host, string username, string password, string[] bypassedAddresses)
        {
            // Arrange
            var settings = Mock.Of<ISettings>();
            var environment = new Mock<IEnvironmentVariableReader>(MockBehavior.Strict);
            environment.Setup(s => s.GetEnvironmentVariable("http_proxy")).Returns(input);
            environment.Setup(s => s.GetEnvironmentVariable("no_proxy")).Returns(string.Join(",", bypassedAddresses));

            var proxyCache = new ProxyCache(settings, environment.Object);

            // Act
            var proxy = proxyCache.GetUserConfiguredProxy();

            // Assert
            AssertProxy(host, username, password, proxy);
            Assert.Equal(bypassedAddresses, proxy!.BypassList);
        }

        [Fact]
        public void SetOverrideProxySettings_WithValidProxy_SetsOverrideProxy()
        {
            // Arrange
            var settings = Mock.Of<ISettings>();
            var environment = Mock.Of<IEnvironmentVariableReader>();
            var proxyCache = new ProxyCache(settings, environment);
            var proxy = new WebProxy("http://proxy.example.com:8080");
            var credentials = new NetworkCredential("testuser", "testpass");

            // Act
            proxyCache.SetOverrideProxySettings(proxy, credentials);

            // Assert
            var sourceUri = new Uri("http://example.com");
            var result = proxyCache.GetProxy(sourceUri);
            Assert.Same(proxy, result);
            Assert.Same(credentials, proxyCache.GetDefaultProxyCredentials());
        }

        [Fact]
        public void GetProxy_WithOverrideProxy_IgnoresSettingsAndEnvironment()
        {
            // Arrange
            var settings = new Mock<ISettings>(MockBehavior.Strict);
            settings.Setup(s => s.GetSection("config"))
                .Returns(new VirtualSettingSection("config",
                    new AddItem("http_proxy", "http://settings.proxy.com")));

            var environment = new Mock<IEnvironmentVariableReader>(MockBehavior.Strict);
            environment.Setup(s => s.GetEnvironmentVariable("http_proxy")).Returns("http://env.proxy.com");
            environment.Setup(s => s.GetEnvironmentVariable("no_proxy")).Returns("");

            var proxyCache = new ProxyCache(settings.Object, environment.Object);
            var overrideProxy = new WebProxy("http://override.proxy.com");
            var credentials = new NetworkCredential("user", "pass");

            // Act
            proxyCache.SetOverrideProxySettings(overrideProxy, credentials);
            var result = proxyCache.GetProxy(new Uri("http://example.com"));

            // Assert
            Assert.Same(overrideProxy, result);

            // Verify that settings and environment were not accessed after override was set
            settings.Verify(s => s.GetSection("config"), Times.Never);
            environment.Verify(s => s.GetEnvironmentVariable("http_proxy"), Times.Never);
        }

        [Fact]
        public void GetDefaultProxyCredentials_WithOverride_ReturnsCredentials()
        {
            // Arrange
            var settings = Mock.Of<ISettings>();
            var environment = Mock.Of<IEnvironmentVariableReader>();
            var proxyCache = new ProxyCache(settings, environment);
            var proxy = new WebProxy("http://proxy.example.com");
            var credentials = new NetworkCredential("testuser", "testpass");

            // Act
            proxyCache.SetOverrideProxySettings(proxy, credentials);

            // Assert
            var result = proxyCache.GetDefaultProxyCredentials();
            Assert.Same(credentials, result);
        }

        [Fact]
        public void SetOverrideProxySettings_Multiple_Times_UpdatesCorrectly()
        {
            // Arrange
            var settings = Mock.Of<ISettings>();
            var environment = Mock.Of<IEnvironmentVariableReader>();
            var proxyCache = new ProxyCache(settings, environment);

            var firstProxy = new WebProxy("http://first.proxy.com");
            var firstCredentials = new NetworkCredential("user1", "pass1");
            var secondProxy = new WebProxy("http://second.proxy.com");
            var secondCredentials = new NetworkCredential("user2", "pass2");

            // Act
            proxyCache.SetOverrideProxySettings(firstProxy, firstCredentials);
            var firstResult = proxyCache.GetProxy(new Uri("http://example.com"));
            var firstCreds = proxyCache.GetDefaultProxyCredentials();

            proxyCache.SetOverrideProxySettings(secondProxy, secondCredentials);
            var secondResult = proxyCache.GetProxy(new Uri("http://example.com"));
            var secondCreds = proxyCache.GetDefaultProxyCredentials();

            // Assert
            Assert.Same(firstProxy, firstResult);
            Assert.Same(firstCredentials, firstCreds);
            Assert.Same(secondProxy, secondResult);
            Assert.Same(secondCredentials, secondCreds);
        }

        private static void AssertProxy(string proxyAddress, string? username, string? password, WebProxy? actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(proxyAddress, actual.ProxyAddress.OriginalString);

            if (username == null)
            {
                Assert.Null(actual.Credentials);
            }
            else
            {
                Assert.NotNull(actual.Credentials);
                Assert.IsType<NetworkCredential>(actual.Credentials);
                var credentials = (NetworkCredential)actual.Credentials;
                Assert.Equal(username, credentials.UserName);
                Assert.Equal(password, credentials.Password);
            }
        }
    }
}
