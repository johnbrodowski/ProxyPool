using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using ProxyPool;
using Xunit;

namespace ProxyPool.Tests
{
    public class ProxyParsingUnitTests
    {
        private static ProxyEnabledHttpClient CreateClient()
        {
            return new ProxyEnabledHttpClient(
                proxyListUrls: new List<string> { "https://example.com/proxies.txt" },
                testTimeoutSeconds: 1,
                fetchTimeoutSeconds: 5,
                maxParallelTests: 1);
        }

        private static ProxyEnabledHttpClient.ProxyInfo ParseProxy(ProxyEnabledHttpClient client, string proxy)
        {
            MethodInfo method = typeof(ProxyEnabledHttpClient)
                .GetMethod("ParseProxy", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var parsed = method.Invoke(client, new object[] { proxy });
            return Assert.IsType<ProxyEnabledHttpClient.ProxyInfo>(parsed);
        }


        [Fact]
        public void Constructor_ShouldHonorConfiguredUserAgentAndHealthCheckInterval()
        {
            using var client = new ProxyEnabledHttpClient(
                proxyListUrls: new List<string> { "https://example.com/proxies.txt" },
                testTimeoutSeconds: 1,
                fetchTimeoutSeconds: 5,
                maxParallelTests: 1,
                userAgent: "UnitTestAgent/1.0",
                healthCheckIntervalMinutes: 7);

            var userAgentField = typeof(ProxyEnabledHttpClient)
                .GetField("_userAgent", BindingFlags.Instance | BindingFlags.NonPublic);
            var intervalField = typeof(ProxyEnabledHttpClient)
                .GetField("_healthCheckIntervalMinutes", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(userAgentField);
            Assert.NotNull(intervalField);
            Assert.Equal("UnitTestAgent/1.0", userAgentField.GetValue(client));
            Assert.Equal(7, intervalField.GetValue(client));
        }

        [Fact]
        public void ParseProxy_ShouldHandleSchemelessHttpProxy()
        {
            using var client = CreateClient();

            var parsed = ParseProxy(client, "1.2.3.4:8080");

            Assert.Equal("1.2.3.4", parsed.Host);
            Assert.Equal(8080, parsed.Port);
            Assert.Equal(ProxyEnabledHttpClient.ProxyType.Http, parsed.Type);
        }

        [Fact]
        public void ParseProxy_ShouldHandleSchemeAndCredentials()
        {
            using var client = CreateClient();

            var parsed = ParseProxy(client, "socks5://user:pass@1.2.3.4:1080");

            Assert.Equal("1.2.3.4", parsed.Host);
            Assert.Equal(1080, parsed.Port);
            Assert.Equal(ProxyEnabledHttpClient.ProxyType.Socks5, parsed.Type);
            Assert.Equal("user", parsed.Username);
            Assert.Equal("pass", parsed.Password);
        }

        [Fact]
        public void ParseProxy_ShouldDefaultHttpsPortTo443()
        {
            using var client = CreateClient();

            var parsed = ParseProxy(client, "https://1.2.3.4");

            Assert.Equal(443, parsed.Port);
            Assert.Equal(ProxyEnabledHttpClient.ProxyType.Https, parsed.Type);
        }


        [Fact]
        public void ParseProxy_ShouldFallbackForInvalidInput()
        {
            using var client = CreateClient();

            var parsed = ParseProxy(client, "not-a-valid-proxy");

            Assert.Equal("not-a-valid-proxy", parsed.Host);
            Assert.Equal(80, parsed.Port);
            Assert.Equal(ProxyEnabledHttpClient.ProxyType.Http, parsed.Type);
        }


        [Fact]
        public async Task FetchHtmlAsync_ShouldRejectBlockedHost()
        {
            using var client = CreateClient();

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => client.FetchHtmlAsync("http://127.0.0.1"));

            Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task FetchHtmlAsync_ShouldRejectNonHttpScheme()
        {
            using var client = CreateClient();

            await Assert.ThrowsAsync<ArgumentException>(
                () => client.FetchHtmlAsync("ftp://example.com"));
        }

        [Fact]
        public async Task ProxyInfo_RecordMethods_ShouldRemainBoundedUnderConcurrency()
        {
            var proxy = new ProxyEnabledHttpClient.ProxyInfo();

            var tasks = new[]
            {
                Task.Run(() =>
                {
                    for (int i = 0; i < 2000; i++)
                    {
                        proxy.RecordSuccess(TimeSpan.FromMilliseconds(100 + (i % 10)));
                    }
                }),
                Task.Run(() =>
                {
                    for (int i = 0; i < 2000; i++)
                    {
                        proxy.RecordFailure();
                    }
                })
            };

            await Task.WhenAll(tasks);

            Assert.InRange(proxy.ReliabilityScore, 0.0, 1.0);
            Assert.True(proxy.SuccessCount >= 2000);
            Assert.True(proxy.FailureCount >= 0);
            Assert.True(proxy.AverageResponseTime > TimeSpan.Zero);
        }
    }
}
