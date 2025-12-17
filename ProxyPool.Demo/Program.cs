using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ProxyPool;

namespace ProxyPool.Demo
{
    /// <summary>
    /// Interactive demo/test application for ProxyPool
    /// Use this to manually test and see real-time proxy discovery and testing
    /// </summary>
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("==========================================");
            Console.WriteLine("ProxyPool Interactive Demo");
            Console.WriteLine("==========================================\n");

            Console.WriteLine("This demo will:");
            Console.WriteLine("  1. Download proxies from a public source");
            Console.WriteLine("  2. Test proxies in parallel");
            Console.WriteLine("  3. Find working proxies");
            Console.WriteLine("  4. Fetch content through proxies");
            Console.WriteLine("  5. Display detailed statistics\n");

            // Test 1: Basic ProxyEnabledHttpClient functionality
            await TestBasicProxyClient();

            Console.WriteLine("\n\n");

            // Test 2: ProxyManager simplified API
            await TestProxyManager();

            Console.WriteLine("\n\n==========================================");
            Console.WriteLine("Demo completed! Press any key to exit...");
            Console.WriteLine("==========================================");
            Console.ReadKey();
        }

        static async Task TestBasicProxyClient()
        {
            Console.WriteLine("DEMO 1: ProxyEnabledHttpClient - Core API");
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
            Console.WriteLine("Step 1: Fetching content through proxies...");
            Console.WriteLine("           Target: https://example.com");
            Console.WriteLine("           (This will discover and test proxies on first run)\n");

            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            try
            {
                string html = await proxyClient.FetchHtmlAsync("https://example.com", cts.Token);
                sw.Stop();

                if (!string.IsNullOrEmpty(html))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✓ SUCCESS!");
                    Console.ResetColor();
                    Console.WriteLine($"  Fetched: {html.Length:N0} characters");
                    Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0}ms");
                    Console.WriteLine($"  Preview: {html.Substring(0, Math.Min(80, html.Length))}...\n");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"⚠ No content fetched (all proxies may be offline)");
                    Console.ResetColor();
                    Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0}ms\n");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ ERROR: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0}ms\n");
            }

            // Get and display statistics
            Console.WriteLine("Step 2: Analyzing proxy pool statistics...\n");
            var stats = proxyClient.GetStatistics();

            Console.WriteLine("═══════════════════════════════════════════");
            Console.WriteLine("         PROXY POOL STATISTICS");
            Console.WriteLine("═══════════════════════════════════════════");
            Console.WriteLine($"  Total Proxies Discovered: {stats.TotalProxies}");
            Console.WriteLine($"  Healthy Proxies Found:    {stats.HealthyProxies}");
            Console.WriteLine($"  Pool Initialized:         {stats.IsInitialized}");
            Console.WriteLine("───────────────────────────────────────────");

            if (stats.TotalProxies > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ Successfully tested {stats.TotalProxies} proxies");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  ⚠ No proxies were discovered");
                Console.ResetColor();
            }

            if (stats.HealthyProxies > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ Found {stats.HealthyProxies} working proxies!");
                Console.ResetColor();

                Console.WriteLine($"\n  Top {Math.Min(5, stats.TopProxies.Count)} Working Proxies:");
                Console.WriteLine("  ─────────────────────────────────────────");

                int count = 1;
                foreach (var proxy in stats.TopProxies.Take(5))
                {
                    var totalRequests = proxy.SuccessCount + proxy.FailureCount;
                    var successRate = totalRequests > 0 ? (double)proxy.SuccessCount / totalRequests * 100 : 0;

                    Console.WriteLine($"\n  #{count}. {proxy.Address}");
                    Console.WriteLine($"      Type:        {proxy.Type}");
                    Console.WriteLine($"      Reliability: {proxy.ReliabilityScore:P0}");
                    Console.WriteLine($"      Success:     {successRate:F1}% ({proxy.SuccessCount}/{totalRequests})");
                    Console.WriteLine($"      Avg Speed:   {proxy.AverageResponseTime.TotalMilliseconds:F0}ms");
                    count++;
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n  ⚠ No healthy proxies found");
                Console.ResetColor();
                Console.WriteLine("     Possible reasons:");
                Console.WriteLine("     • All proxies from source are offline (common)");
                Console.WriteLine("     • Network/firewall blocking proxy connections");
                Console.WriteLine("     • Proxy source temporarily unavailable");
            }

            Console.WriteLine("═══════════════════════════════════════════\n");
        }

        static async Task TestProxyManager()
        {
            Console.WriteLine("DEMO 2: ProxyManager - Simplified API");
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
            Console.WriteLine("\nStep 1: Initializing proxy pool...");
            Console.WriteLine("           (Finding at least 5 working proxies)\n");

            var sw = Stopwatch.StartNew();

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                bool initialized = await proxyManager.InitializeProxyPoolAsync(cts.Token);
                sw.Stop();

                if (initialized)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✓ SUCCESS: Proxy pool initialized!");
                    Console.ResetColor();
                    Console.WriteLine($"  Time: {sw.Elapsed.TotalSeconds:F1} seconds\n");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"⚠ Partial initialization");
                    Console.ResetColor();
                    Console.WriteLine($"  Time: {sw.Elapsed.TotalSeconds:F1} seconds");
                    Console.WriteLine($"  Note: May not have found minimum required proxies\n");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ ERROR: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine($"  Time: {sw.Elapsed.TotalSeconds:F1} seconds\n");
                return;
            }

            // Test fetching content
            Console.WriteLine("Step 2: Fetching content using ProxyManager...");
            Console.WriteLine("           Target: https://example.com\n");

            sw.Restart();

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                string html = await proxyManager.FetchHtmlAsync("https://example.com", cts.Token);
                sw.Stop();

                if (!string.IsNullOrEmpty(html))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✓ SUCCESS!");
                    Console.ResetColor();
                    Console.WriteLine($"  Fetched: {html.Length:N0} characters");
                    Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0}ms\n");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"⚠ No content fetched");
                    Console.ResetColor();
                    Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0}ms\n");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ ERROR: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0}ms\n");
            }

            // Get statistics
            Console.WriteLine("Step 3: Final statistics...\n");
            var stats = proxyManager.GetStatistics();

            Console.WriteLine("═══════════════════════════════════════════");
            Console.WriteLine("      PROXYMANAGER STATISTICS");
            Console.WriteLine("═══════════════════════════════════════════");
            Console.WriteLine($"  Total Proxies:   {stats.TotalProxies}");
            Console.WriteLine($"  Healthy Proxies: {stats.HealthyProxies}");

            if (stats.HealthyProxies > 0)
            {
                var successRate = stats.HealthyProxies * 100.0 / stats.TotalProxies;
                Console.WriteLine($"  Success Rate:    {successRate:F1}%");
                Console.WriteLine("───────────────────────────────────────────");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  ✓ ProxyManager is working correctly!");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine("───────────────────────────────────────────");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  ⚠ No healthy proxies available");
                Console.ResetColor();
            }

            Console.WriteLine("═══════════════════════════════════════════");
        }
    }
}
