namespace BIManage.Revit.Protection
{
    /// <summary>
    ///     Intervention mode for command protection
    /// </summary>
    public enum InterventionMode
    {
        /// <summary>
        ///     Notify only - log command execution, no blocking
        /// </summary>
        Notify = 0,

        /// <summary>
        ///     Assist mode - show TaskDialog with Proceed/Cancel option
        /// </summary>
        Assist = 1,

        /// <summary>
        ///     Protect mode - show TaskDialog, require password/OTP to proceed
        /// </summary>
        Protect = 2
    }
}
