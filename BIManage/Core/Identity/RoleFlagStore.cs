using System;

namespace BIManage.Core.Identity
{
    /// <summary>
    /// Cross-ALC role flag store. On Revit 2025 the BIManageRevit assembly is loaded twice
    /// (Default ALC + BIManageRevit ALC) — see [AsmIdentity] DUPLICATE LOAD warnings — so any
    /// static state inside the plugin (Application.Instance, UserService, ServiceRegistry) is
    /// duplicated and the IExternalCommandAvailability classes Revit instantiates may run from
    /// the ALC where Application.Instance is null. This store sidesteps that by writing primitive
    /// bools to AppDomain data, which is shared across ALCs because the dictionary itself lives
    /// on System.AppDomain in the runtime's base library.
    ///
    /// Writers: UserService (SetCurrentUser, UpdateAdminFlags, Logout, ctor).
    /// Readers: AvailableOnlyToAdmins, AvailableOnlyToProjectAdmins, AvailableOnlyToCompanyAdmins,
    ///          AvailableToAdminsWithActiveDocument.
    /// </summary>
    public static class RoleFlagStore
    {
        private const string KeyIsCompanyAdmin = "BIManage.Role.IsCompanyAdmin";
        private const string KeyIsProjectAdmin = "BIManage.Role.IsProjectAdmin";

        public static void SetFlags(bool isCompanyAdmin, bool isProjectAdmin)
        {
            AppDomain.CurrentDomain.SetData(KeyIsCompanyAdmin, isCompanyAdmin);
            AppDomain.CurrentDomain.SetData(KeyIsProjectAdmin, isProjectAdmin);
        }

        public static bool IsCompanyAdmin =>
            AppDomain.CurrentDomain.GetData(KeyIsCompanyAdmin) is bool b && b;

        public static bool IsProjectAdmin =>
            AppDomain.CurrentDomain.GetData(KeyIsProjectAdmin) is bool b && b;

        public static bool HasAdminPrivileges => IsCompanyAdmin || IsProjectAdmin;
    }
}
