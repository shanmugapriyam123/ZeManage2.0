using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Autodesk.Revit.DB;
using BIManage.Core.Rules.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BIManage.Views.Protection
{
    /// <summary>
    /// ViewModel for modeless guidance dialog
    /// Displays non-blocking guidance without requiring immediate user response
    /// </summary>
    public partial class ModelessGuideViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _title = "Guidance";

        [ObservableProperty]
        private string _message;

        [ObservableProperty]
        private string _ruleNames;

        [ObservableProperty]
        private string _elementInfo;

        [ObservableProperty]
        private bool _isVisible = true;

        [ObservableProperty]
        private int _elementCount;

        [ObservableProperty]
        private string _commandName;

        private readonly Action _onClose;

        public ModelessGuideViewModel(
            RuleEvaluationResult result,
            List<ElementId> elementIds,
            Action onClose = null)
        {
            _onClose = onClose;

            // Set message from rule evaluation
            Message = result.CombinedMessage ?? "This action may require review.";

            // Set rule names
            if (result.MatchedRules != null && result.MatchedRules.Count > 0)
            {
                RuleNames = string.Join(", ", result.MatchedRules.Select(r => r.Name));
            }
            else
            {
                RuleNames = "No specific rules";
            }

            // Set element info
            ElementCount = elementIds?.Count ?? 0;
            if (ElementCount > 0)
            {
                ElementInfo = $"{ElementCount} element{(ElementCount != 1 ? "s" : "")} affected";
            }
            else
            {
                ElementInfo = "No elements selected";
            }

            // Set command name if available
            CommandName = "Command in progress";
        }

        [RelayCommand]
        private void Acknowledge()
        {
            IsVisible = false;
            _onClose?.Invoke();
        }

        [RelayCommand]
        private void Dismiss()
        {
            IsVisible = false;
            _onClose?.Invoke();
        }

        /// <summary>
        /// Update the guidance message (for real-time updates)
        /// </summary>
        public void UpdateMessage(string newMessage)
        {
            Message = newMessage;
        }

        /// <summary>
        /// Update element count (for multi-step operations)
        /// </summary>
        public void UpdateElementCount(int count)
        {
            ElementCount = count;
            ElementInfo = $"{ElementCount} element{(ElementCount != 1 ? "s" : "")} affected";
        }
    }
}
