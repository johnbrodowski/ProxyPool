using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ProxyPool;

namespace ProxyPool.Tests
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("==========================================");
            Console.WriteLine("ProxyPool Functionality Test");
            Console.WriteLine("==========================================\n");

            // Test 1: Basic ProxyEnabledHttpClient functionality
            await TestBasicProxyClient();

            Console.WriteLine("\n\n");

            // Test 2: ProxyManager simplified API
            await TestProxyManager();

            Console.WriteLine("\n\n==========================================");
            Console.WriteLine("All tests completed!");
            Console.WriteLine("==========================================");
        }

        static async Task TestBasicProxyClient()
        {
            Console.WriteLine("TEST 1: ProxyEnabledHttpClient - Basic Functionality");
            Console.WriteLine("---------------------------------------------");

            var proxyListUrls = new List<string>
            {
                "https://raw.githubusercontent.com/MuRongPIG/Proxy-Master/main/socks5.txt"
            };

            Console.WriteLine($"✓ Using proxy source: {proxyListUrls[0]}");
            Console.WriteLine("\nInitializing ProxyEnabledHttpClient...");

            using var proxyClient = new ProxyEnabledHttpClient(
                proxyListUrls: proxyListUrls,
                testTimeoutSeconds: 10,
                fetchTimeoutSeconds: 30,
                maxParallelTests: 50,
                maxRetries: 2,
                allowDirectFallback: false
            );

            Console.WriteLine("✓ ProxyEnabledHttpClient created\n");

            // Test fetching content
            Console.WriteLine("Test 1.1: Fetching content through proxies...");
            Console.WriteLine("Target URL: https://example.com");

            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            try
            {
                string html = await proxyClient.FetchHtmlAsync("https://example.com", cts.Token);
                sw.Stop();

                if (!string.IsNullOrEmpty(html))
                {
                    Console.WriteLine($"✓ SUCCESS: Fetched {html.Length} characters in {sw.ElapsedMilliseconds}ms");
                    Console.WriteLine($"  Content preview: {html.Substring(0, Math.Min(100, html.Length))}...\n");
                }
                else
                {
                    Console.WriteLine($"✗ FAILED: No content fetched after {sw.ElapsedMilliseconds}ms\n");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"✗ ERROR: {ex.Message} (after {sw.ElapsedMilliseconds}ms)\n");
            }

            // Get and display statistics
            Console.WriteLine("Test 1.2: Checking proxy pool statistics...");
            var stats = proxyClient.GetStatistics();

            Console.WriteLine($"\nProxy Pool Statistics:");
            Console.WriteLine($"  Total Proxies: {stats.TotalProxies}");
            Console.WriteLine($"  Healthy Proxies: {stats.HealthyProxies}");
            Console.WriteLine($"  Initialized: {stats.IsInitialized}");

            if (stats.TotalProxies > 0)
            {
                Console.WriteLine($"  ✓ Successfully discovered and tested {stats.TotalProxies} proxies");
            }
            else
            {
                Console.WriteLine("  ✗ WARNING: No proxies were added to the pool");
            }

            if (stats.HealthyProxies > 0)
            {
                Console.WriteLine($"  ✓ Found {stats.HealthyProxies} working proxies!");

                Console.WriteLine($"\n  Top {Math.Min(5, stats.TopProxies.Count)} Working Proxies:");
                int count = 1;
                foreach (var proxy in stats.TopProxies.Take(5))
                {
                    var totalRequests = proxy.SuccessCount + proxy.FailureCount;
                    var successRate = totalRequests > 0 ? (double)proxy.SuccessCount / totalRequests * 100 : 0;

                    Console.WriteLine($"  {count}. {proxy.Address}");
                    Console.WriteLine($"     Type: {proxy.Type}");
                    Console.WriteLine($"     Reliability: {proxy.ReliabilityScore:P0}");
                    Console.WriteLine($"     Success Rate: {successRate:F1}% ({proxy.SuccessCount}/{totalRequests})");
                    Console.WriteLine($"     Avg Response: {proxy.AverageResponseTime.TotalMilliseconds:F0}ms");
                    count++;
                }
            }
            else
            {
                Console.WriteLine("  ✗ WARNING: No healthy proxies found");
                Console.WriteLine("     This could mean:");
                Console.WriteLine("     - All proxies from the source are currently offline");
                Console.WriteLine("     - Network connectivity issues");
                Console.WriteLine("     - Proxy source is not accessible");
            }
        }

        static async Task TestProxyManager()
        {
            Console.WriteLine("TEST 2: ProxyManager - Simplified API");
            Console.WriteLine("---------------------------------------------");

            var proxyListUrls = new List<string>
            {
                "https://raw.githubusercontent.com/MuRongPIG/Proxy-Master/main/socks5.txt"
            };

            Console.WriteLine($"✓ Using proxy source: {proxyListUrls[0]}");
            Console.WriteLine("\nInitializing ProxyManager...");

            var proxyManager = new ProxyManager(
                proxyListUrls: proxyListUrls,
                minProxiesToFind: 5,
                maxProxyTestTimeSeconds: 60
            );

            Console.WriteLine("✓ ProxyManager created");

            // Test initialization
            Console.WriteLine("\nTest 2.1: Initializing proxy pool...");
            var sw = Stopwatch.StartNew();

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                bool initialized = await proxyManager.InitializeProxyPoolAsync(cts.Token);
                sw.Stop();

                if (initialized)
                {
                    Console.WriteLine($"✓ SUCCESS: Proxy pool initialized in {sw.Elapsed.TotalSeconds:F1} seconds");
                }
                else
                {
                    Console.WriteLine($"✗ WARNING: Initialization completed but may not have found enough proxies ({sw.Elapsed.TotalSeconds:F1}s)");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"✗ ERROR: {ex.Message} (after {sw.Elapsed.TotalSeconds:F1}s)");
                return;
            }

            // Test fetching content
            Console.WriteLine("\nTest 2.2: Fetching content using ProxyManager...");
            sw.Restart();

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                string html = await proxyManager.FetchHtmlAsync("https://example.com", cts.Token);
                sw.Stop();

                if (!string.IsNullOrEmpty(html))
                {
                    Console.WriteLine($"✓ SUCCESS: Fetched {html.Length} characters in {sw.ElapsedMilliseconds}ms");
                }
                else
                {
                    Console.WriteLine($"✗ FAILED: No content fetched after {sw.ElapsedMilliseconds}ms");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"✗ ERROR: {ex.Message} (after {sw.ElapsedMilliseconds}ms)");
            }

            // Get statistics
            Console.WriteLine("\nTest 2.3: ProxyManager statistics...");
            var stats = proxyManager.GetStatistics();

            Console.WriteLine($"\nFinal Statistics:");
            Console.WriteLine($"  Total Proxies: {stats.TotalProxies}");
            Console.WriteLine($"  Healthy Proxies: {stats.HealthyProxies}");

            if (stats.HealthyProxies > 0)
            {
                var successRate = stats.HealthyProxies * 100.0 / stats.TotalProxies;
                Console.WriteLine($"  Success Rate: {successRate:F1}%");
                Console.WriteLine($"  ✓ ProxyManager is working correctly!");
            }
            else
            {
                Console.WriteLine("  ✗ No healthy proxies available");
            }
        }
    }
}
