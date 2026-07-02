using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BIManage.Core.Rules.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.Caching
{
    /// <summary>
    /// Hybrid cache manager combining in-memory cache with SQLite persistence
    /// Provides fast access with persistence fallback and offline support
    /// Thread-safe with LRU eviction and automatic invalidation
    /// </summary>
    public class HybridCacheManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _databasePath;
        private readonly RuleRepository _ruleRepository;

        // In-memory cache layers
        private readonly ConcurrentDictionary<string, CachedItem<Rule>> _ruleCache;
        private readonly ConcurrentDictionary<string, DateTime> _cacheAccessTimes;

        // Cache configuration
        private readonly int _maxInMemoryItems;
        private readonly TimeSpan _defaultTtl;
        private readonly TimeSpan _cleanupInterval;

        // Background cleanup
        private readonly Timer _cleanupTimer;
        private readonly SemaphoreSlim _cacheLock;

        // Statistics
        private long _hitCount;
        private long _missCount;
        private long _evictionCount;

        private bool _disposed;

        public HybridCacheManager(
            string databasePath,
            ILogger logger,
            int maxInMemoryItems = 1000,
            TimeSpan? defaultTtl = null,
            TimeSpan? cleanupInterval = null)
        {
            _databasePath = databasePath;
            _logger = logger;
            _maxInMemoryItems = maxInMemoryItems;
            _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(30);
            _cleanupInterval = cleanupInterval ?? TimeSpan.FromMinutes(5);

            _ruleCache = new ConcurrentDictionary<string, CachedItem<Rule>>();
            _cacheAccessTimes = new ConcurrentDictionary<string, DateTime>();
            _cacheLock = new SemaphoreSlim(1, 1);

            _ruleRepository = new RuleRepository(databasePath, logger);

            // Start background cleanup (fire-and-forget to avoid blocking)
            _cleanupTimer = new Timer(
                callback: _ => Task.Run(async () =>
                {
                    try
                    {
                        await CleanupExpiredItemsAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"Cache cleanup failed: {ex.Message}");
                    }
                }),
                state: null,
                dueTime: _cleanupInterval,
                period: _cleanupInterval);

            _logger?.LogInfo($"HybridCacheManager initialized (MaxItems: {_maxInMemoryItems}, TTL: {_defaultTtl.TotalMinutes}min)");
        }

        #region Get Operations

        /// <summary>
        /// Get rule from cache with multi-tier fallback
        /// 1. In-memory cache
        /// 2. SQLite cache
        /// 3. Load from source (if loader provided)
        /// </summary>
        public async Task<Rule> GetRuleAsync(string ruleId, Func<Task<Rule>> loader = null)
        {
            // Tier 1: In-memory cache
            if (_ruleCache.TryGetValue(ruleId, out var cachedItem))
            {
                if (!cachedItem.IsExpired)
                {
                    UpdateAccessTime(ruleId);
                    Interlocked.Increment(ref _hitCount);
                    _logger?.LogDebug($"Cache hit (memory): {ruleId}");
                    return cachedItem.Value;
                }
                else
                {
                    // Remove expired item
                    _ruleCache.TryRemove(ruleId, out _);
                    _logger?.LogDebug($"Expired cache entry removed: {ruleId}");
                }
            }

            // Tier 2: SQLite cache
            try
            {
                var rule = await _ruleRepository.GetRuleByIdAsync(ruleId);
                if (rule != null)
                {
                    // Promote to in-memory cache
                    await SetAsync(ruleId, rule, _defaultTtl);
                    Interlocked.Increment(ref _hitCount);
                    _logger?.LogDebug($"Cache hit (SQLite): {ruleId}");
                    return rule;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to retrieve from SQLite cache: {ex.Message}");
            }

            // Tier 3: Load from source
            Interlocked.Increment(ref _missCount);

            if (loader != null)
            {
                try
                {
                    var rule = await loader();
                    if (rule != null)
                    {
                        await SetAsync(ruleId, rule, _defaultTtl);
                        _logger?.LogDebug($"Cache miss - loaded from source: {ruleId}");
                        return rule;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to load from source: {ex.Message}", ex);
                }
            }

            return null;
        }

        /// <summary>
        /// Get all rules from cache (memory + SQLite)
        /// </summary>
        public async Task<List<Rule>> GetAllRulesAsync(bool memoryOnly = false)
        {
            var rules = new List<Rule>();

            // Get from memory cache
            var memoryRules = _ruleCache.Values
                .Where(item => !item.IsExpired)
                .Select(item => item.Value)
                .ToList();

            rules.AddRange(memoryRules);
            _logger?.LogDebug($"Retrieved {memoryRules.Count} rules from memory cache");

            // Get from SQLite if requested
            if (!memoryOnly)
            {
                try
                {
                    var sqliteRules = await _ruleRepository.GetAllRulesAsync();

                    // Add SQLite rules not in memory
                    var memoryRuleIds = new HashSet<string>(memoryRules.Select(r => r.RuleId));
                    var additionalRules = sqliteRules.Where(r => !memoryRuleIds.Contains(r.RuleId)).ToList();

                    rules.AddRange(additionalRules);
                    _logger?.LogDebug($"Retrieved {additionalRules.Count} additional rules from SQLite cache");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to retrieve from SQLite: {ex.Message}");
                }
            }

            return rules;
        }

        #endregion

        #region Set Operations

        /// <summary>
        /// Set value in cache (both memory and SQLite)
        /// </summary>
        public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? ttl = null) where T : Rule
        {
            var effectiveTtl = ttl ?? _defaultTtl;

            try
            {
                // Check if eviction needed (LRU)
                await EnsureCapacityAsync();

                // Add to memory cache
                var cacheItem = new CachedItem<Rule>
                {
                    Value = value,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.Add(effectiveTtl)
                };

                _ruleCache.AddOrUpdate(key, cacheItem, (k, old) => cacheItem);
                UpdateAccessTime(key);

                // Persist to SQLite
                await _ruleRepository.SaveRuleAsync(value);

                _logger?.LogDebug($"Cached: {key} (TTL: {effectiveTtl.TotalMinutes}min)");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to cache item: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Set multiple rules in batch (performance optimization)
        /// </summary>
        public async Task<int> SetBatchAsync(List<Rule> rules, TimeSpan? ttl = null)
        {
            var effectiveTtl = ttl ?? _defaultTtl;
            int successCount = 0;

            try
            {
                await EnsureCapacityAsync(rules.Count);

                var cacheTime = DateTime.UtcNow;
                var expiryTime = cacheTime.Add(effectiveTtl);

                // Add to memory cache
                foreach (var rule in rules)
                {
                    var cacheItem = new CachedItem<Rule>
                    {
                        Value = rule,
                        CreatedAt = cacheTime,
                        ExpiresAt = expiryTime
                    };

                    _ruleCache.AddOrUpdate(rule.RuleId, cacheItem, (k, old) => cacheItem);
                    UpdateAccessTime(rule.RuleId);
                    successCount++;
                }

                // Persist to SQLite (one by one since no batch method exists)
                foreach (var rule in rules)
                {
                    await _ruleRepository.SaveRuleAsync(rule);
                }

                _logger?.LogInfo($"Batch cached {successCount} rules");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to batch cache: {ex.Message}", ex);
            }

            return successCount;
        }

        #endregion

        #region Invalidation

        /// <summary>
        /// Invalidate specific cache entry
        /// </summary>
        public async Task<bool> InvalidateAsync(string key)
        {
            try
            {
                _ruleCache.TryRemove(key, out _);
                _cacheAccessTimes.TryRemove(key, out _);

                // Mark as stale in SQLite (don't delete for offline support)
                // await MarkStaleInSQLite(key);

                _logger?.LogDebug($"Invalidated cache: {key}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to invalidate: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Invalidate all cache entries
        /// </summary>
        public async Task<bool> InvalidateAllAsync()
        {
            try
            {
                var count = _ruleCache.Count;
                _ruleCache.Clear();
                _cacheAccessTimes.Clear();

                _logger?.LogInfo($"Invalidated all cache entries ({count} items)");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to invalidate all: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Invalidate cache entries matching predicate
        /// </summary>
        public async Task<int> InvalidateWhereAsync(Func<Rule, bool> predicate)
        {
            int invalidatedCount = 0;

            try
            {
                var keysToRemove = _ruleCache
                    .Where(kvp => predicate(kvp.Value.Value))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _ruleCache.TryRemove(key, out _);
                    _cacheAccessTimes.TryRemove(key, out _);
                    invalidatedCount++;
                }

                _logger?.LogInfo($"Invalidated {invalidatedCount} cache entries matching predicate");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to invalidate with predicate: {ex.Message}", ex);
            }

            return invalidatedCount;
        }

        #endregion

        #region Cleanup & Eviction

        /// <summary>
        /// Cleanup expired cache entries
        /// </summary>
        private async Task CleanupExpiredItemsAsync()
        {
            try
            {
                var expiredKeys = _ruleCache
                    .Where(kvp => kvp.Value.IsExpired)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _ruleCache.TryRemove(key, out _);
                    _cacheAccessTimes.TryRemove(key, out _);
                    Interlocked.Increment(ref _evictionCount);
                }

                if (expiredKeys.Count > 0)
                {
                    _logger?.LogDebug($"Cleaned up {expiredKeys.Count} expired cache entries");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error during cleanup: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Ensure cache capacity using LRU eviction
        /// </summary>
        private async Task EnsureCapacityAsync(int additionalItems = 1)
        {
            if (_ruleCache.Count + additionalItems <= _maxInMemoryItems)
                return;

            await _cacheLock.WaitAsync();
            try
            {
                var itemsToEvict = _ruleCache.Count + additionalItems - _maxInMemoryItems;
                if (itemsToEvict <= 0)
                    return;

                // Evict LRU items
                var lruKeys = _cacheAccessTimes
                    .OrderBy(kvp => kvp.Value)
                    .Take(itemsToEvict)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in lruKeys)
                {
                    _ruleCache.TryRemove(key, out _);
                    _cacheAccessTimes.TryRemove(key, out _);
                    Interlocked.Increment(ref _evictionCount);
                }

                _logger?.LogDebug($"Evicted {lruKeys.Count} LRU cache entries");
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// Update access time for LRU tracking
        /// </summary>
        private void UpdateAccessTime(string key)
        {
            _cacheAccessTimes.AddOrUpdate(key, DateTime.UtcNow, (k, old) => DateTime.UtcNow);
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public CacheStatistics GetStatistics()
        {
            var totalAccess = _hitCount + _missCount;
            var hitRate = totalAccess > 0 ? (double)_hitCount / totalAccess * 100 : 0;

            return new CacheStatistics
            {
                InMemoryCount = _ruleCache.Count,
                MaxCapacity = _maxInMemoryItems,
                HitCount = _hitCount,
                MissCount = _missCount,
                EvictionCount = _evictionCount,
                HitRate = hitRate,
                MemoryUsageKb = EstimateMemoryUsage()
            };
        }

        private long EstimateMemoryUsage()
        {
            // Rough estimation: 2KB per rule on average
            return _ruleCache.Count * 2;
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _cleanupTimer?.Dispose();
            _cacheLock?.Dispose();
            _ruleCache?.Clear();
            _cacheAccessTimes?.Clear();

            _disposed = true;
            _logger?.LogInfo("HybridCacheManager disposed");
        }
    }

    #region Helper Classes

    /// <summary>
    /// Cached item wrapper with expiration
    /// </summary>
    internal class CachedItem<T>
    {
        public T Value { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }

        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
        public TimeSpan Age => DateTime.UtcNow - CreatedAt;
        public TimeSpan TimeToLive => ExpiresAt - DateTime.UtcNow;
    }

    /// <summary>
    /// Cache statistics model
    /// </summary>
    public class CacheStatistics
    {
        public int InMemoryCount { get; set; }
        public int MaxCapacity { get; set; }
        public long HitCount { get; set; }
        public long MissCount { get; set; }
        public long EvictionCount { get; set; }
        public double HitRate { get; set; }
        public long MemoryUsageKb { get; set; }

        public double FillRate => MaxCapacity > 0 ? (double)InMemoryCount / MaxCapacity * 100 : 0;
        public long TotalAccess => HitCount + MissCount;

        public override string ToString()
        {
            return $"Cache: {InMemoryCount}/{MaxCapacity} items ({FillRate:F1}% full) | Hit Rate: {HitRate:F1}% | Memory: {MemoryUsageKb}KB";
        }
    }

    #endregion
}
