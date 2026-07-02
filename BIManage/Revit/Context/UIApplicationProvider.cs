using System;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Context
{
    /// <summary>
    ///     Holds UIApplication reference and notifies services when it becomes available
    /// </summary>
    public class UIApplicationProvider : IUIApplicationProvider
    {
        private readonly ILogger? _logger;
        private UIApplication? _uiApplication;
        private bool _isSet;

        public UIApplicationProvider(ILogger? logger)
        {
            _logger = logger;
        }

        public UIApplication? UIApplication => _uiApplication;

        public bool IsAvailable => _isSet && _uiApplication != null;

        public void SetUIApplication(UIApplication uiApplication)
        {
            if (_isSet)
            {
                _logger?.LogWarning("UIApplication already set, ignoring duplicate call");
                return;
            }

            _uiApplication = uiApplication ?? throw new ArgumentNullException(nameof(uiApplication));
            _isSet = true;
            _logger?.LogInfo("UIApplication registered - command and idling services can now be initialized");
        }
    }
}
