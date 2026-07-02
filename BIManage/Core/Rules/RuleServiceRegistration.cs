using System;
using System.IO;
using System.Threading.Tasks;
using BIManage.Core.Rules;
using BIManage.Core.Rules.Cache;
using BIManage.Core.Rules.Evaluation;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.Threading;

namespace BIManage.Core.Rules
{
    /// <summary>
    /// Extension methods for registering rule engine services
    /// </summary>
    public static class RuleServiceRegistration
    {
        /// <summary>
        /// Register all rule engine services with the service registry
        /// </summary>
        /// <param name="services">The service registry</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="databasePath">Path to SQLite database (optional, uses default if null)</param>
        /// <param name="samplesPath">Path to sample rules JSON files (optional, uses default if null)</param>
        /// <returns>The service registry for chaining</returns>
        public static ServiceRegistry RegisterRuleServices(
            this ServiceRegistry services,
            ILogger logger,
            string? databasePath = null,
            string? samplesPath = null)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            logger.LogInfo("Registering Rule Engine services...");

            try
            {
                // Determine database path
                if (string.IsNullOrEmpty(databasePath))
                {
                    var appDataPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BIManageRevit"
                    );

                    // Ensure directory exists
                    if (!Directory.Exists(appDataPath))
                    {
                        Directory.CreateDirectory(appDataPath);
                        logger.LogInfo($"Created application data directory: {appDataPath}");
                    }

                    databasePath = Path.Combine(appDataPath, "BIManage.db");
                }

                logger.LogInfo($"Using database: {databasePath}");

                // Determine samples path
                if (string.IsNullOrEmpty(samplesPath))
                {
                    samplesPath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "BIManage",
                        "Rules",
                        "Samples"
                    );
                }

                logger.LogInfo($"Using samples path: {samplesPath}");

                // Register evaluators (leaf services first)
                services.RegisterSingleton<ICategoryEvaluator>(new CategoryEvaluator(logger));
                logger.LogDebug("Registered ICategoryEvaluator");

                services.RegisterSingleton<IParameterEvaluator>(new ParameterEvaluator(logger));
                logger.LogDebug("Registered IParameterEvaluator");

                services.RegisterSingleton<ITypeEvaluator>(new TypeEvaluator(logger));
                logger.LogDebug("Registered ITypeEvaluator");

                // Register main evaluator (depends on above evaluators)
                var categoryEvaluator = services.GetRequiredService<ICategoryEvaluator>();
                var parameterEvaluator = services.GetRequiredService<IParameterEvaluator>();
                var typeEvaluator = services.GetRequiredService<ITypeEvaluator>();

                services.RegisterSingleton<IRuleEvaluator>(
                    new RuleEvaluator(categoryEvaluator, parameterEvaluator, typeEvaluator, logger)
                );
                logger.LogDebug("Registered IRuleEvaluator");

                // Register repository
                var integrityService = services.GetService<global::BIManage.Data.SQLite.DatabaseIntegrityService>();
                var ruleRepository = new RuleRepository(databasePath, logger, integrityService);
                services.RegisterSingleton(ruleRepository);
                logger.LogDebug("Registered RuleRepository");

                // Register cache manager (depends on repository)
                services.RegisterSingleton<IRuleCacheManager>(
                    new HybridRuleCacheManager(ruleRepository, logger)
                );
                logger.LogDebug("Registered IRuleCacheManager");

                // Register main service (depends on all above)
                var ruleEvaluator = services.GetRequiredService<IRuleEvaluator>();
                var cacheManager = services.GetRequiredService<IRuleCacheManager>();

                services.RegisterSingleton<IRuleService>(
                    new RuleService(ruleEvaluator, cacheManager, ruleRepository, logger, samplesPath)
                );
                logger.LogDebug("Registered IRuleService");

                logger.LogInfo("Rule Engine services registered successfully");
                return services;
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to register Rule Engine services: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Initialize the rule service (must be called after registration)
        /// Uses fire-and-forget pattern to prevent blocking Revit startup
        /// </summary>
        /// <param name="services">The service registry</param>
        /// <param name="logger">Logger instance</param>
        public static void InitializeRuleService(this ServiceRegistry services, ILogger logger)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            var ruleService = services.GetService<IRuleService>();

            if (ruleService == null)
            {
                logger.LogError("IRuleService not registered. Call RegisterRuleServices first.");
                return;
            }

            // Initialize asynchronously using fire-and-forget to prevent blocking Revit startup
            TaskRunner.FireAndForget(
                InitializeRuleServiceAsync(ruleService, logger),
                logger,
                "RuleService.Initialize");
        }

        /// <summary>
        /// Async helper for rule service initialization
        /// </summary>
        private static async Task InitializeRuleServiceAsync(IRuleService ruleService, ILogger logger)
        {
            try
            {
                logger.LogInfo("Initializing Rule Service (async)...");

                var success = await ruleService.InitializeAsync().ConfigureAwait(false);

                if (success)
                {
                    var stats = ruleService.GetCacheStatistics();
                    logger.LogInfo($"Rule Service initialized successfully: {stats}");
                }
                else
                {
                    logger.LogError("Rule Service initialization failed");
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to initialize Rule Service: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Validate that all rule services are registered correctly
        /// </summary>
        /// <param name="services">The service registry</param>
        /// <param name="logger">Logger instance</param>
        /// <returns>True if all services are registered</returns>
        public static bool ValidateRuleServices(this ServiceRegistry services, ILogger logger)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            logger.LogDebug("Validating Rule Engine service registration...");

            var allValid = true;

            // Check each required service
            if (services.GetService<ICategoryEvaluator>() == null)
            {
                logger.LogError("ICategoryEvaluator not registered");
                allValid = false;
            }

            if (services.GetService<IParameterEvaluator>() == null)
            {
                logger.LogError("IParameterEvaluator not registered");
                allValid = false;
            }

            if (services.GetService<ITypeEvaluator>() == null)
            {
                logger.LogError("ITypeEvaluator not registered");
                allValid = false;
            }

            if (services.GetService<IRuleEvaluator>() == null)
            {
                logger.LogError("IRuleEvaluator not registered");
                allValid = false;
            }

            if (services.GetService<RuleRepository>() == null)
            {
                logger.LogError("RuleRepository not registered");
                allValid = false;
            }

            if (services.GetService<IRuleCacheManager>() == null)
            {
                logger.LogError("IRuleCacheManager not registered");
                allValid = false;
            }

            if (services.GetService<IRuleService>() == null)
            {
                logger.LogError("IRuleService not registered");
                allValid = false;
            }

            if (allValid)
            {
                logger.LogDebug("All Rule Engine services validated successfully");
            }
            else
            {
                logger.LogError("Rule Engine service validation failed");
            }

            return allValid;
        }
    }
}