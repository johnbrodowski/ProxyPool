using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Timer = System.Threading.Timer;

namespace ProxyPool
{
    /// <summary>
    /// A robust HTTP client that automatically manages and rotates through proxies
    /// </summary>
    public class ProxyEnabledHttpClient : IDisposable
    {
        #region Configuration Settings

        private IEnumerable<string> _proxyListUrls;
        private int _testTimeoutSeconds;
        private int _fetchTimeoutSeconds;
        private int _maxParallelTests;
        private int _maxRetries;
        private bool _allowDirectFallback;
        private string _userAgent;
        private int _healthCheckIntervalMinutes;

        #endregion Configuration Settings

        #region Internal State

        private readonly ConcurrentDictionary<string, ProxyInfo> _proxyPool = new ConcurrentDictionary<string, ProxyInfo>();
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly HttpClient _directClient;
        private Timer _healthCheckTimer;
        private bool _isInitialized;
        private bool _isDisposed;
        private readonly string[] _testUrls = { "https://html.duckduckgo.com/html/", "https://duckduckgo.com/" };

        #endregion Internal State

        #region Constructor

        /// <summary>
        /// Creates a new instance of ProxyEnabledHttpClient
        /// </summary>
        /// <param name="proxyListUrls">URLs to fetch proxy lists from</param>
        /// <param name="testTimeoutSeconds">Timeout for proxy testing in seconds</param>
        /// <param name="fetchTimeoutSeconds">Timeout for fetch operations in seconds</param>
        /// <param name="maxParallelTests">Maximum number of parallel proxy tests</param>
        /// <param name="maxRetries">Maximum number of retries per proxy</param>
        /// <param name="healthCheckIntervalMinutes">Interval for proxy health checks in minutes</param>
        /// <param name="allowDirectFallback">Whether to allow direct connection if all proxies fail</param>
        /// <param name="userAgent">User agent string to use for requests</param>
        public ProxyEnabledHttpClient(
            IEnumerable<string> proxyListUrls,
            int testTimeoutSeconds,
            int fetchTimeoutSeconds,
            int maxParallelTests,
            int maxRetries = 3,
            bool allowDirectFallback = false,
            string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36")
        {
            int healthCheckIntervalMinutes = 30;


            // Validate inputs
            _proxyListUrls = proxyListUrls ?? throw new ArgumentNullException(nameof(proxyListUrls));
            if (!_proxyListUrls.Any())
            {
                throw new ArgumentException("At least one proxy list URL must be provided.", nameof(proxyListUrls));
            }


            // Store configuration
            _testTimeoutSeconds = Math.Max(1, testTimeoutSeconds);
            _fetchTimeoutSeconds = Math.Max(5, fetchTimeoutSeconds);
            _maxParallelTests = Math.Max(1, Math.Min(500, maxParallelTests)); // Limit between 1 and 50
            _maxRetries = Math.Max(1, maxRetries);
            _healthCheckIntervalMinutes = Math.Max(5, healthCheckIntervalMinutes);
            _allowDirectFallback = allowDirectFallback;
            _userAgent = "Mozilla/5.0";

            // Set up direct client with compression support
            _directClient = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });

            // Set up periodic health check
            _healthCheckTimer = new Timer(
                PerformHealthCheck,
                null,
                TimeSpan.FromMinutes(_healthCheckIntervalMinutes),
                TimeSpan.FromMinutes(_healthCheckIntervalMinutes));
        }

        #endregion Constructor

        #region Public Methods

        /// <summary>
        /// Fetch HTML content from a URL using available proxies
        /// </summary>
        /// <param name="url">The URL to fetch</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>HTML content as string, or empty string if failed</returns>
        public async Task<string> FetchHtmlAsync(string url = "https://duckduckgo.com/", CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException("URL cannot be null or empty", nameof(url));
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                // Try with existing known healthy proxies first
                var result = await TryWithHealthyProxiesAsync(url, cancellationToken);
                if (!string.IsNullOrEmpty(result))
                {
                    LogInfo($"Successfully fetched {url} with known proxy in {stopwatch.ElapsedMilliseconds}ms");
                    return result;
                }

                // If no working proxies or all failed, find and test new ones
                result = await TryWithNewProxiesAsync(url, cancellationToken);
                if (!string.IsNullOrEmpty(result))
                {
                    LogInfo($"Successfully fetched {url} with new proxy in {stopwatch.ElapsedMilliseconds}ms");
                    return result;
                }

                // Try direct connection as a last resort if allowed
                if (_allowDirectFallback)
                {
                    LogWarning($"All proxies failed for {url}, trying direct connection");
                    result = await FetchWithDirectConnectionAsync(url, cancellationToken);
                    if (!string.IsNullOrEmpty(result))
                    {
                        LogInfo($"Successfully fetched {url} via direct connection in {stopwatch.ElapsedMilliseconds}ms");
                        return result;
                    }
                }

                LogError($"Failed to fetch {url} after trying all available proxies" +
                         (_allowDirectFallback ? " and direct connection" : ""));
                return string.Empty;
            }
            catch (OperationCanceledException)
            {
                LogInfo($"Operation for {url} was canceled after {stopwatch.ElapsedMilliseconds}ms");
                return string.Empty;
            }
            catch (Exception ex)
            {
                LogError($"Unexpected error fetching {url}: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Get statistics about the current proxy pool
        /// </summary>
        /// <returns>ProxyPoolStatistics object with current stats</returns>
        public ProxyPoolStatistics GetStatistics()
        {
            var stats = new ProxyPoolStatistics
            {
                TotalProxies = _proxyPool.Count,
                HealthyProxies = _proxyPool.Values.Count(p => p.IsHealthy),
                IsInitialized = _isInitialized,
                TopProxies = _proxyPool.Values
                    .OrderByDescending(p => p.ReliabilityScore)
                    .ThenBy(p => p.AverageResponseTime)
                    .Take(10)
                    .Select(p => new ProxyStatistics
                    {
                        Address = p.Address,
                        SuccessCount = p.SuccessCount,
                        FailureCount = p.FailureCount,
                        ReliabilityScore = p.ReliabilityScore,
                        AverageResponseTime = p.AverageResponseTime,
                        Type = p.Type.ToString()
                    })
                    .ToList()
            };

            return stats;
        }


        #endregion Public Methods

        #region Private Helper Methods

        private async Task<string> TryWithHealthyProxiesAsync(string url, CancellationToken cancellationToken)
        {
            // Get healthy proxies sorted by reliability
            var healthyProxies = _proxyPool.Values
                .Where(p => p.IsHealthy)
                .OrderByDescending(p => p.ReliabilityScore)
                .ThenBy(p => p.AverageResponseTime)
                .Take(5)
                .ToList();

            // Try each healthy proxy
            foreach (var proxy in healthyProxies)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return string.Empty;
                }

                string result = await TryFetchWithProxyAsync(url, proxy, cancellationToken);
                if (!string.IsNullOrEmpty(result))
                {
                    return result;
                }
            }

            return string.Empty;
        }

        private async Task<string> TryWithNewProxiesAsync(string url, CancellationToken cancellationToken)
        {
            bool lockTaken = false;
            try
            {
                // Use a timeout to avoid waiting forever
                lockTaken = await _initLock.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                if (!lockTaken) return string.Empty;

                List<string> proxyAddresses;

                if (!_isInitialized)
                {
                    // Fetch proxy lists if not initialized
                    proxyAddresses = await FetchAllProxyListsAsync(cancellationToken);
                    LogInfo($"Found {proxyAddresses.Count} potential proxies to check");
                    _isInitialized = true;
                }
                else
                {
                    // Use untested proxies or those that haven't failed too many times
                    proxyAddresses = _proxyPool.Values
                        .Where(p => !p.IsHealthy && p.FailureCount < 3)
                        .Select(p => p.Address)
                        .ToList();


                    proxyAddresses.Reverse();

                    // If we don't have many untested proxies, fetch more
                    if (proxyAddresses.Count < 100)
                    {
                        var newProxies = await FetchAllProxyListsAsync(cancellationToken);
                        proxyAddresses.AddRange(newProxies.Where(p => !_proxyPool.ContainsKey(p)));
                        proxyAddresses = proxyAddresses.Distinct().ToList();
                    }
                }

                // Release lock before potentially long operation
                _initLock.Release();
                lockTaken = false;

                // Find the best working proxy
                var workingProxy = await FindBestWorkingProxyAsync(
                    proxyAddresses,
                    cancellationToken,
                    minProxiesToCheck: 50,  // Check at least 1000 proxies
                    maxTimeSeconds: 240       // Spend at most 60 seconds checking
                );

                if (workingProxy != null)
                {
                    // Try to fetch with the working proxy
                    string result = await FetchWithProxyAsync(url, workingProxy, cancellationToken);
                    if (!string.IsNullOrEmpty(result))
                    {
                        return result;
                    }
                }

                return string.Empty;
            }
            finally
            {
                // Make sure we release the lock if we still hold it
                if (lockTaken)
                {
                    _initLock.Release();
                }
            }
        }

        private async Task<ProxyInfo> FindBestWorkingProxyAsync(
            List<string> proxyAddresses,
            CancellationToken cancellationToken,
            int minProxiesToCheck,
            int maxTimeSeconds)
        {
            LogInfo($"Testing proxies (up to {_maxParallelTests} in parallel)...");

            // Create a thread-safe collection to store working proxies
            var workingProxies = new ConcurrentBag<ProxyInfo>();

            // Create a semaphore to limit parallel tests (don't use 'using' - we'll dispose it manually after all tasks complete)
            var semaphore = new SemaphoreSlim(_maxParallelTests);

            // Create a set to track which proxies we've already tried
            var triedProxies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Use a stopwatch to limit total time spent checking proxies
            var stopwatch = Stopwatch.StartNew();

            // Track how many proxies we've started checking
            int proxiesStartedChecking = 0;

            // Create a completion signal for when we have enough working proxies
            var earlyCompletionSource = new TaskCompletionSource<bool>();

            // Create a timeout task
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(maxTimeSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            // Create a collection to track all running tasks
            var tasks = new List<Task>();

            // Shuffle the proxy addresses to avoid testing them in the same order every time

            foreach (string address in proxyAddresses)
            {
                // Stop if operation was canceled or we've timed out
                if (linkedCts.Token.IsCancellationRequested || stopwatch.Elapsed.TotalSeconds > maxTimeSeconds)
                {
                    break;
                }

                // Stop if we've checked the minimum number of proxies AND have at least 5 working proxies
                if (proxiesStartedChecking >= minProxiesToCheck && workingProxies.Count >= 10)

                {
                    LogInfo($"Found {workingProxies.Count} working proxies after checking {proxiesStartedChecking} proxies, stopping search");
                    break;
                }

                // Skip if we already have this proxy in our pool and it's marked as unhealthy
                if (_proxyPool.TryGetValue(address, out var existingProxy) && !existingProxy.IsHealthy)
                {
                    continue;
                }

                // Skip if we've already tried this proxy in this session
                if (triedProxies.Contains(address))
                {
                    continue;
                }

                proxiesStartedChecking++;
                triedProxies.Add(address);

                // Wait for a slot in the semaphore
                try
                {
                    await semaphore.WaitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Start a task to test this proxy
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        // Skip if operation canceled
                        if (linkedCts.Token.IsCancellationRequested)
                        {
                            return;
                        }

                        ProxyInfo proxyInfo = ParseProxy(address);
                        bool isWorking = false;

                        // Try with multiple test URLs
                        foreach (var testUrl in _testUrls)
                        {
                            try
                            {
                                // Skip if operation canceled
                                if (linkedCts.Token.IsCancellationRequested)
                                {
                                    break;
                                }

                                using var testTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_testTimeoutSeconds));
                                using var testLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                                    testTimeoutCts.Token, linkedCts.Token);

                                if (await TestProxyAsync(proxyInfo, testUrl, testLinkedCts.Token))
                                {
                                    isWorking = true;
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                // Log all exceptions and continue to next URL
                                LogDebug($"Error testing proxy {address} with {testUrl}: {ex.Message}");
                                continue;
                            }
                        }

                        if (isWorking)
                        {
                            proxyInfo.RecordSuccess();
                            _proxyPool.AddOrUpdate(address, proxyInfo, (key, old) =>
                            {
                                old.RecordSuccess();
                                return old;
                            });
                            LogInfo($"✓ Found working proxy: {address} ({proxyInfo.Type})");

                            // Add to the collection of working proxies
                            workingProxies.Add(proxyInfo);

                            // Signal early completion if we have enough working proxies
                            if (workingProxies.Count >= 5 && proxiesStartedChecking >= minProxiesToCheck)
                            {
                                earlyCompletionSource.TrySetResult(true);
                            }
                        }
                        else
                        {
                            // Add to pool as unhealthy to avoid rechecking
                            proxyInfo.RecordFailure();
                            _proxyPool.AddOrUpdate(address, proxyInfo, (key, old) =>
                            {
                                old.RecordFailure();
                                return old;
                            });
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        // Catch ALL exceptions, log, and continue
                        LogDebug($"Error processing proxy {address}: {ex.Message}");
                    }
                    finally
                    {
                        // Always release the semaphore
                        semaphore.Release();
                    }
                }, linkedCts.Token));

                // Every 100 proxies, clean up completed tasks to avoid memory issues
                if (proxiesStartedChecking % 500 == 0)
                {
                    // Wait for already completed tasks
                    var completedTasks = tasks.Where(t => t.IsCompleted).ToArray();
                    if (completedTasks.Length > 0)
                    {
                        await Task.WhenAll(completedTasks);
                        tasks.RemoveAll(t => t.IsCompleted);
                    }

                    LogDebug($"Checked {proxiesStartedChecking} proxies so far, found {workingProxies.Count} working ones");
                }

                // Check if we've hit early completion or timeout
                if (earlyCompletionSource.Task.IsCompleted || timeoutCts.Token.IsCancellationRequested)
                {
                    break;
                }
            }

            // Wait for all running tasks or timeout
            try
            {
                await Task.WhenAny(
                    Task.WhenAll(tasks),
                    earlyCompletionSource.Task,
                    Task.Delay(TimeSpan.FromSeconds(5), linkedCts.Token)
                );
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation exceptions
            }

            // IMPORTANT: Wait for ALL tasks to complete before disposing the semaphore
            // This prevents ObjectDisposedException when tasks try to release the semaphore
            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // Ignore all exceptions from tasks - we've already handled them
            }

            // Now it's safe to dispose the semaphore since all tasks have completed
            semaphore.Dispose();

            LogInfo($"Checked {proxiesStartedChecking} proxies and found {workingProxies.Count} working ones");

            // Return the best proxy based on reliability and response time
            return workingProxies
                .OrderByDescending(p => p.ReliabilityScore)
                .ThenBy(p => p.AverageResponseTime)
                .FirstOrDefault();
        }

        private async Task<bool> TestProxyAsync(ProxyInfo proxy, string testUrl, CancellationToken cancellationToken)
        {
            try
            {
                using var client = CreateHttpClient(proxy);
                var response = await client.GetAsync(testUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                // Just return false on any exception
                return false;
            }
        }

        private async Task<string> FetchWithProxyAsync(string url, ProxyInfo proxy, CancellationToken cancellationToken)
        {
            try
            {
                using var client = CreateHttpClient(proxy);

                // Use the configurable fetch timeout
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_fetchTimeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

                Stopwatch sw = Stopwatch.StartNew();
                var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, linkedCts.Token);
                sw.Stop();

                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(linkedCts.Token);
                    proxy.RecordSuccess(sw.Elapsed);
                    return content;
                }

                proxy.RecordFailure();
                LogDebug($"Failed to fetch {url} with proxy {proxy.Address}. Status: {response.StatusCode}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                proxy.RecordFailure();
                LogDebug($"Error fetching {url} with proxy {proxy.Address}: {ex.Message}");
                return string.Empty;
            }
        }

        private async Task<string> TryFetchWithProxyAsync(string url, ProxyInfo proxy, CancellationToken cancellationToken)
        {
            // Use the configurable number of retries
            for (int attempt = 0; attempt < _maxRetries; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return string.Empty;
                }

                try
                {
                    string result = await FetchWithProxyAsync(url, proxy, cancellationToken);
                    if (!string.IsNullOrEmpty(result))
                    {
                        return result;
                    }

                    LogDebug($"Attempt {attempt + 1} with proxy {proxy.Address} failed for {url}");
                }
                catch (Exception ex)
                {
                    LogDebug($"Error during attempt {attempt + 1} with proxy {proxy.Address} for {url}: {ex.Message}");
                }

                // Exponential backoff before retry
                if (attempt < _maxRetries - 1)
                {
                    try
                    {
                        int delay = (int)(200 * Math.Pow(2, attempt));
                        await Task.Delay(delay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Operation was canceled during delay, exit retry loop
                        break;
                    }
                }
            }

            // All retries failed, mark proxy as failed
            proxy.RecordFailure();
            return string.Empty;
        }


        private ProxyInfo ParseProxy(string proxyAddress)
        {
            // Initialize defaults
            string host = proxyAddress;
            int port = 80;
            ProxyType proxyType = ProxyType.Http;
            string username = null;
            string password = null;

            try
            {
                // Handle authentication format: username:password@host:port
                if (host.Contains('@'))
                {
                    string[] authParts = host.Split('@');
                    if (authParts.Length == 2)
                    {
                        string[] credentials = authParts[0].Split(':');
                        if (credentials.Length == 2)
                        {
                            username = credentials[0];
                            password = credentials[1];
                        }
                        host = authParts[1];
                    }
                }

                // Parse host:port format
                string[] parts = host.Split(':');
                if (parts.Length >= 2)
                {
                    host = parts[0];
                    if (int.TryParse(parts[1], out int parsedPort))
                    {
                        port = parsedPort;
                    }
                }

                // Determine proxy type from address if specified
                if (proxyAddress.Contains("socks5", StringComparison.OrdinalIgnoreCase))
                {
                    proxyType = ProxyType.Socks5;
                }
                else if (proxyAddress.Contains("socks4", StringComparison.OrdinalIgnoreCase))
                {
                    proxyType = ProxyType.Socks4;
                }
                else if (proxyAddress.Contains("https", StringComparison.OrdinalIgnoreCase))
                {
                    proxyType = ProxyType.Https;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error parsing proxy address {proxyAddress}: {ex.Message}");
                // Use defaults if parsing fails
            }

            return new ProxyInfo
            {
                Address = proxyAddress,
                Host = host,
                Port = port,
                Type = proxyType,
                Username = username,
                Password = password
            };
        }

        private async Task<string> FetchWithDirectConnectionAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_fetchTimeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

                var response = await _directClient.GetAsync(url, linkedCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(linkedCts.Token);
                }

                LogDebug($"Failed to fetch {url} with direct connection. Status: {response.StatusCode}");
                return string.Empty;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                LogDebug($"Error fetching {url} with direct connection: {ex.Message}");
                return string.Empty;
            }
        }

        private HttpClient CreateHttpClient(ProxyInfo proxy)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                UseCookies = true
            };

            if (proxy == null)
            {
                handler.UseProxy = false;
                var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(_fetchTimeoutSeconds)
                };
                AddDefaultHeaders(client);
                return client;
            }

            var webProxy = new WebProxy(proxy.Host, proxy.Port);

            if (!string.IsNullOrEmpty(proxy.Username) && !string.IsNullOrEmpty(proxy.Password))
            {
                webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
            }

            handler.Proxy = webProxy;
            handler.UseProxy = true;

            var proxyClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_fetchTimeoutSeconds)
            };

            AddDefaultHeaders(proxyClient);
            return proxyClient;
        }

        private void AddDefaultHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.Add("User-Agent", _userAgent);
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
        }

        private void PerformHealthCheck(object state)
        {
            // We can't use async directly in the timer callback, so we'll start a task
            Task.Run(async () =>
            {
                try
                {
                    LogDebug("Starting periodic proxy health check");

                    int healthyBefore = _proxyPool.Values.Count(p => p.IsHealthy);

                    // Focus health checks on proxies that need verification
                    var proxiesToCheck = _proxyPool.Values
                        .Where(p =>
                            // Proxies not checked recently
                            (DateTime.UtcNow - p.LastChecked) > TimeSpan.FromMinutes(30) ||
                            // Unhealthy proxies that might have recovered
                            (!p.IsHealthy && p.FailureCount < 5))
                        .ToList();

                    // Use a semaphore to limit concurrent health checks
                    using var semaphore = new SemaphoreSlim(_maxParallelTests);
                    var tasks = new List<Task>();

                    foreach (var proxy in proxiesToCheck)
                    {
                        await semaphore.WaitAsync();

                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                foreach (var testUrl in _testUrls)
                                {
                                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_testTimeoutSeconds));
                                    bool isWorking = await TestProxyAsync(proxy, testUrl, cts.Token);
                                    if (isWorking)
                                    {
                                        proxy.RecordSuccess();
                                        LogDebug($"Health check: Proxy {proxy.Address} is healthy");
                                        break;
                                    }
                                    else
                                    {
                                        proxy.RecordFailure();
                                        LogDebug($"Health check: Proxy {proxy.Address} is unhealthy");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                LogDebug($"Error during health check for {proxy.Address}: {ex.Message}");
                                proxy.RecordFailure();
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }));
                    }

                    // Wait for all health check tasks to complete with a timeout
                    try
                    {
                        await Task.WhenAny(
                            Task.WhenAll(tasks),
                            Task.Delay(TimeSpan.FromMinutes(5))
                        );
                    }
                    catch (Exception ex)
                    {
                        LogError($"Error waiting for health check tasks: {ex.Message}");
                    }

                    // Clean up permanently failed proxies
                    RemoveFailedProxies();

                    int healthyAfter = _proxyPool.Values.Count(p => p.IsHealthy);
                    LogDebug($"Health check completed. Healthy proxies: {healthyBefore} -> {healthyAfter}");
                }
                catch (Exception ex)
                {
                    LogError($"Error in proxy health check: {ex.Message}");
                }
            });
        }

        private void RemoveFailedProxies()
        {
            // Remove proxies with excessive failures and low reliability
            var failedProxies = _proxyPool.Where(kv =>
                    kv.Value.FailureCount > 10 &&
                    kv.Value.ReliabilityScore < 0.1 &&
                    (DateTime.UtcNow - kv.Value.LastSuccess) > TimeSpan.FromHours(1))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in failedProxies)
            {
                _proxyPool.TryRemove(key, out _);
            }

            if (failedProxies.Count > 0)
            {
                LogInfo($"Removed {failedProxies.Count} permanently failed proxies from the pool");
            }
        }

        #endregion Private Helper Methods

        #region Logging Methods

        private void LogDebug(string message)
        {
            Debug.WriteLine($"[DEBUG] {DateTime.Now}: {message}");
        }

        private void LogInfo(string message)
        {
            Debug.WriteLine($"[INFO] {DateTime.Now}: {message}");
            Console.WriteLine($"[INFO] {message}");  // Also output to console
        }

        private void LogWarning(string message)
        {
            Debug.WriteLine($"[WARNING] {DateTime.Now}: {message}");
            Console.WriteLine($"[WARNING] {message}");  // Also output to console
        }

        private void LogError(string message)
        {
            Debug.WriteLine($"[ERROR] {DateTime.Now}: {message}");
            Console.WriteLine($"[ERROR] {message}");  // Also output to console
        }

        #endregion Logging Methods

        #region IDisposable Implementation

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                _initLock?.Dispose();
                _directClient?.Dispose();
                _healthCheckTimer?.Dispose();
            }

            _isDisposed = true;
        }

        #endregion IDisposable Implementation

        /// <summary>
        /// Fetches proxy lists from all configured sources
        /// </summary>
        private async Task<List<string>> FetchAllProxyListsAsync(CancellationToken cancellationToken)
        {
            var allProxies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

            // Create tasks to fetch from all sources in parallel
            var fetchTasks = _proxyListUrls.Select(url => Task.Run(async () =>
            {
                try
                {
                    string response = await client.GetStringAsync(url, cancellationToken);
                    var proxies = response
                        .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => p.Contains(':') && !string.IsNullOrWhiteSpace(p))
                        .Select(p => p.Trim())
                        .ToList();

                    Debug.WriteLine($"Fetched {proxies.Count} proxies from {url}");
                    return proxies;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error fetching from {url}: {ex.Message}");
                    return new List<string>();
                }
            }, cancellationToken)).ToList();

            try
            {
                // Wait for all tasks or until timeout
                await Task.WhenAll(fetchTasks);

                // Combine all results
                foreach (var task in fetchTasks)
                {
                    foreach (var proxy in task.Result)
                    {
                        allProxies.Add(proxy);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during proxy list fetching: {ex.Message}");
            }

            return allProxies.ToList();
        }

        #region Supporting Classes

        /// <summary>
        /// Represents information about a proxy server
        /// </summary>
        public class ProxyInfo
        {
            public string Address { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
            public ProxyType Type { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public double ReliabilityScore { get; set; } = 0.5;
            public int FailureCount { get; set; }
            public int SuccessCount { get; set; }
            public DateTime LastChecked { get; set; } = DateTime.UtcNow;
            public DateTime LastSuccess { get; set; } = DateTime.UtcNow;
            public TimeSpan AverageResponseTime { get; set; } = TimeSpan.FromSeconds(1);
            private readonly Queue<TimeSpan> _responseTimes = new Queue<TimeSpan>(10); // Track last 10 response times

            public bool IsHealthy => FailureCount < 3 && ReliabilityScore > 0.3;

            public void RecordSuccess(TimeSpan? responseTime = null)
            {
                SuccessCount++;
                FailureCount = Math.Max(0, FailureCount - 1); // Reduce failure count on success
                ReliabilityScore = Math.Min(1.0, ReliabilityScore + 0.1);
                LastChecked = DateTime.UtcNow;
                LastSuccess = DateTime.UtcNow;

                if (responseTime.HasValue)
                {
                    // Update average response time
                    _responseTimes.Enqueue(responseTime.Value);
                    if (_responseTimes.Count > 10)
                    {
                        _responseTimes.Dequeue();
                    }

                    if (_responseTimes.Count > 0)
                    {
                        AverageResponseTime = TimeSpan.FromMilliseconds(
                            _responseTimes.Average(t => t.TotalMilliseconds));
                    }
                }
            }

            public void RecordFailure()
            {
                FailureCount++;
                ReliabilityScore = Math.Max(0.0, ReliabilityScore - 0.2);
                LastChecked = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Types of proxy servers
        /// </summary>
        public enum ProxyType
        {
            Http,
            Https,
            Socks4,
            Socks5
        }

        /// <summary>
        /// Statistics about the proxy pool
        /// </summary>
        public class ProxyPoolStatistics
        {
            public int TotalProxies { get; set; }
            public int HealthyProxies { get; set; }
            public bool IsInitialized { get; set; }
            public List<ProxyStatistics> TopProxies { get; set; } = new List<ProxyStatistics>();
        }

        /// <summary>
        /// Statistics about a specific proxy
        /// </summary>
        public class ProxyStatistics
        {
            public string Address { get; set; }
            public int SuccessCount { get; set; }
            public int FailureCount { get; set; }
            public double ReliabilityScore { get; set; }
            public TimeSpan AverageResponseTime { get; set; }
            public string Type { get; set; }
        }

        #endregion Supporting Classes
    }


    public class ProxyManager
    {
        private readonly ProxyEnabledHttpClient _proxyClient;
        private readonly List<string> _proxyListUrls;
        private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);
        private bool _isInitialized = false;
        private readonly int _minProxiesToFind;
        private readonly int _maxProxyTestTimeSeconds;

        public ProxyManager(
            List<string> proxyListUrls,
            int minProxiesToFind = 20,
            int maxProxyTestTimeSeconds = 120)
        {
            _proxyListUrls = proxyListUrls ?? throw new ArgumentNullException(nameof(proxyListUrls));
            _minProxiesToFind = Math.Max(5, minProxiesToFind);
            _maxProxyTestTimeSeconds = Math.Max(30, maxProxyTestTimeSeconds);

            // Create proxy client with configuration optimized for proxy discovery
            _proxyClient = new ProxyEnabledHttpClient(
                proxyListUrls: proxyListUrls,
                testTimeoutSeconds:7,          // Faster timeout for testing
                fetchTimeoutSeconds: 20,
                maxParallelTests: 50,          // Higher parallelism for faster testing
                maxRetries: 2,                  // Fewer retries during initialization
                allowDirectFallback: false
            );
        }

        /// <summary>
        /// Initializes the proxy pool by finding and validating proxies.
        /// This will be called automatically on first use, or can be called manually.
        /// </summary>
        public async Task<bool> InitializeProxyPoolAsync(CancellationToken cancellationToken = default)
        {
            // Use a lock to prevent multiple simultaneous initializations
            bool lockTaken = false;
            try
            {
                lockTaken = await _initializationLock.WaitAsync(TimeSpan.FromSeconds(40), cancellationToken);
                if (!lockTaken) return false;

                if (_isInitialized)
                {
                    Debug.WriteLine("Proxy pool is already initialized.");
                    return true;
                }

                Debug.WriteLine("Starting proxy pool initialization...");

                // Fetch proxy lists from all sources
                List<string> allProxies = await FetchAllProxyListsAsync(cancellationToken);
                Debug.WriteLine($"Fetched {allProxies.Count} proxy addresses from {_proxyListUrls.Count} sources");

                if (allProxies.Count == 0)
                {
                    Debug.WriteLine("Failed to fetch any proxies from the provided sources");
                    return false;
                }

                // Test proxies to find working ones
                await TestProxiesAsync(allProxies, cancellationToken);

                // Get statistics
                var stats = _proxyClient.GetStatistics();

                Debug.WriteLine($"Proxy pool initialization completed:");
                Debug.WriteLine($"- Total proxies tested: {allProxies.Count}");
                Debug.WriteLine($"- Working proxies found: {stats.HealthyProxies}");

                if (stats.HealthyProxies >= _minProxiesToFind)
                {
                    _isInitialized = true;
                    Debug.WriteLine("Proxy pool successfully initialized");

                    // Log the top working proxies
                    Debug.WriteLine("\nTop working proxies:");
                    foreach (var proxy in stats.TopProxies.Take(5))
                    {
                        Debug.WriteLine($"- {proxy.Address} (Reliability: {proxy.ReliabilityScore:F2}, Type: {proxy.Type})");
                    }

                    return true;
                }
                else
                {
                    Debug.WriteLine($"Warning: Only found {stats.HealthyProxies} working proxies, which is below the minimum threshold of {_minProxiesToFind}");
                    return stats.HealthyProxies > 0;
                }
            }
            finally
            {
                // Always release the lock
                if (lockTaken)
                {
                    _initializationLock.Release();
                }
            }
        }

        /// <summary>
        /// Fetches proxy lists from all configured sources
        /// </summary>
        private async Task<List<string>> FetchAllProxyListsAsync(CancellationToken cancellationToken)
        {
            var allProxies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

            // Create tasks to fetch from all sources in parallel
            var fetchTasks = _proxyListUrls.Select(url => Task.Run(async () =>
            {
                try
                {
                    string response = await client.GetStringAsync(url, cancellationToken);
                    var proxies = response
                        .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => p.Contains(':') && !string.IsNullOrWhiteSpace(p))
                        .Select(p => p.Trim())
                        .ToList();

                    Debug.WriteLine($"Fetched {proxies.Count} proxies from {url}");
                    return proxies;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error fetching from {url}: {ex.Message}");
                    return new List<string>();
                }
            }, cancellationToken)).ToList();

            try
            {
                // Wait for all tasks or until timeout
                await Task.WhenAll(fetchTasks);

                // Combine all results
                foreach (var task in fetchTasks)
                {
                    foreach (var proxy in task.Result)
                    {
                        allProxies.Add(proxy);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during proxy list fetching: {ex.Message}");
            }

            return allProxies.ToList();
        }

        /// <summary>
        /// Tests proxies to find working ones
        /// </summary>
        private async Task TestProxiesAsync(List<string> proxies, CancellationToken cancellationToken)
        {
            // Shuffle the proxies to avoid testing them all in the same order
            var shuffledProxies = proxies.OrderBy(_ => Guid.NewGuid()).ToList();

            Debug.WriteLine($"Testing {shuffledProxies.Count} proxies...");

            // To test proxies, we'll make a dummy request that will trigger the proxy testing
            string dummyUrl = "https://duckduckgo.com/";

            // Use a timeout for the entire testing process
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_maxProxyTestTimeSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            try
            {
                // This will trigger proxy testing internally
                string result = await _proxyClient.FetchHtmlAsync(dummyUrl, linkedCts.Token);

                if (string.IsNullOrEmpty(result))
                {
                    Debug.WriteLine("Warning: Initial proxy test failed to return content");
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Proxy testing was canceled or timed out");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during proxy testing: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the proxy client, initializing it first if needed
        /// </summary>
        public async Task<ProxyEnabledHttpClient> GetProxyClientAsync(CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
            {
                await InitializeProxyPoolAsync(cancellationToken);
            }

            return _proxyClient;
        }

        /// <summary>
        /// Gets statistics about the current proxy pool
        /// </summary>
        public ProxyEnabledHttpClient.ProxyPoolStatistics GetStatistics()
        {
            return _proxyClient.GetStatistics();
        }

        /// <summary>
        /// Fetches HTML content from a URL using the proxy client
        /// </summary>
        public async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
            {
                await InitializeProxyPoolAsync(cancellationToken);
            }

            return await _proxyClient.FetchHtmlAsync(url, cancellationToken);
        }
    }
}
