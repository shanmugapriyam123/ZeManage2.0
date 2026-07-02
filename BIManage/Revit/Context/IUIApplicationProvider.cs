using Autodesk.Revit.UI;

namespace BIManage.Revit.Context
{
    /// <summary>
    ///     Provides access to UIApplication when it becomes available
    ///     UIApplication is not available during OnStartup, only after ApplicationInitialized or in commands
    /// </summary>
    public interface IUIApplicationProvider
    {
        /// <summary>
        ///     UIApplication instance (null until set)
        /// </summary>
        UIApplication? UIApplication { get; }

        /// <summary>
        ///     Set the UIApplication instance (called once when available)
        /// </summary>
        void SetUIApplication(UIApplication uiApplication);

        /// <summary>
        ///     Check if UIApplication is available
        /// </summary>
        bool IsAvailable { get; }
    }
}
