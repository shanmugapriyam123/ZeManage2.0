using System;
using System.Globalization;
using System.Windows.Data;
using BIManage.Core.Rules.Models;

namespace BIManageRevit.BIManage.Views.Converters
{
    /// <summary>
    /// Converts RuleScope integer to display string
    /// </summary>
    public class RuleScopeToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "Company";

            int scopeInt;
            if (value is int i)
                scopeInt = i;
            else if (value is string s && int.TryParse(s, out var parsed))
                scopeInt = parsed;
            else
                return "Company";

            return scopeInt switch
            {
                (int)RuleScopeType.CompanyWide => "Company",
                (int)RuleScopeType.ProjectWide => "Project",
                (int)RuleScopeType.ModelSpecific => "Model",
                _ => "Company"
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string displayStr)
            {
                return displayStr switch
                {
                    "Company" => (int)RuleScopeType.CompanyWide,
                    "Project" => (int)RuleScopeType.ProjectWide,
                    "Model" => (int)RuleScopeType.ModelSpecific,
                    _ => (int)RuleScopeType.CompanyWide
                };
            }
            return (int)RuleScopeType.CompanyWide;
        }
    }
}
