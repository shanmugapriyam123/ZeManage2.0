using System.Collections.Generic;

namespace BIManage.Core.Features
{
    /// <summary>
    ///     Interface for managing feature toggles and pause states.
    ///     Allows enabling/disabling features without code changes.
    /// </summary>
    public interface IFeatureToggleService
    {
        /// <summary>
        ///     Global pause flag - when true, all monitoring is paused.
        /// </summary>
        bool IsGlobalPaused { get; set; }

        /// <summary>
        ///     Check if a specific feature is enabled.
        /// </summary>
        /// <param name="featureName">The feature name to check</param>
        /// <returns>True if the feature is enabled, false otherwise</returns>
        bool IsFeatureEnabled(string featureName);

        /// <summary>
        ///     Enable or disable a specific feature.
        /// </summary>
        /// <param name="featureName">The feature name</param>
        /// <param name="enabled">True to enable, false to disable</param>
        void SetFeatureEnabled(string featureName, bool enabled);

        /// <summary>
        ///     Get all feature states (for debugging/admin purposes).
        /// </summary>
        /// <returns>Dictionary of feature names and their enabled states</returns>
        Dictionary<string, bool> GetAllFeatureStates();
    }
}
