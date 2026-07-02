using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Autodesk.Revit.DB;
using BIManage.Core.Rules.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BIManage.ViewModels.Protection
{
    /// <summary>
    /// Base ViewModel for all protection dialogs
    /// </summary>
    public abstract class ProtectionDialogBaseViewModel : ObservableObject
    {
        private RuleEvaluationResult _evaluationResult;
        private ICollection<ElementId> _affectedElements;
        private string _dialogTitle;
        private string _dialogMessage;

        public RuleEvaluationResult EvaluationResult
        {
            get => _evaluationResult;
            set => SetProperty(ref _evaluationResult, value);
        }

        public ICollection<ElementId> AffectedElements
        {
            get => _affectedElements;
            set => SetProperty(ref _affectedElements, value);
        }

        public string DialogTitle
        {
            get => _dialogTitle;
            set => SetProperty(ref _dialogTitle, value);
        }

        public string DialogMessage
        {
            get => _dialogMessage;
            set
            {
                if (SetProperty(ref _dialogMessage, value))
                    OnPropertyChanged(nameof(HasDialogMessage));
            }
        }

        /// <summary>
        /// True when DialogMessage has visible text. The XAML message panel binds its
        /// Visibility to this so the empty light-blue box doesn't render for rules that
        /// have no Message configured (e.g. Cable Trays — only required-comment, no message).
        /// </summary>
        public bool HasDialogMessage => !string.IsNullOrWhiteSpace(_dialogMessage);

        public ObservableCollection<RuleDisplayInfo> MatchedRules { get; } = new ObservableCollection<RuleDisplayInfo>();

        public string ElementSummary => GetElementSummary();

        public int ElementCount => AffectedElements?.Count ?? 0;

        public string ProtectionModeText => EvaluationResult?.FinalMode.ToString() ?? "Unknown";

        protected ProtectionDialogBaseViewModel()
        {
        }

        public virtual void Initialize(RuleEvaluationResult result, ICollection<ElementId> elements)
        {
            EvaluationResult = result;
            AffectedElements = elements;

            MatchedRules.Clear();
            if (result?.MatchedRules != null)
            {
                foreach (var rule in result.MatchedRules)
                {
                    MatchedRules.Add(new RuleDisplayInfo
                    {
                        Name = rule.Name,
                        Description = rule.Description,
                        Mode = rule.Mode,
                        Message = rule.Message
                    });
                }
            }

            OnPropertyChanged(nameof(ElementSummary));
            OnPropertyChanged(nameof(ElementCount));
            OnPropertyChanged(nameof(ProtectionModeText));
        }

        private string GetElementSummary()
        {
            if (AffectedElements == null || AffectedElements.Count == 0)
                return "No elements selected";

            var count = AffectedElements.Count;
            if (count == 1)
                return "1 element selected";
            
            return $"{count} elements selected";
        }
    }

    /// <summary>
    /// Display information for a matched rule
    /// </summary>
    public class RuleDisplayInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ProtectionMode Mode { get; set; }
        public string Message { get; set; }
        public string ModeIcon => GetModeIcon();
        public string ModeColor => GetModeColor();

        private string GetModeIcon()
        {
            return Mode switch
            {
                ProtectionMode.Notify => "👁",
                ProtectionMode.Assist => "⚠",
                ProtectionMode.Protect => "🛑",
                _ => "?"
            };
        }

        private string GetModeColor()
        {
            return Mode switch
            {
                ProtectionMode.Notify => "#2196F3",
                ProtectionMode.Assist => "#FF9800",
                ProtectionMode.Protect => "#F44336",
                _ => "#757575"
            };
        }
    }
}
