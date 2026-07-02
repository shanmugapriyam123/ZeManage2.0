using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using BIManage.Core.Rules.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BIManage.ViewModels.Protection
{
    /// <summary>
    /// Unified dialog mode: Notify (green), Assist (blue), or Protect (red)
    /// </summary>
    public enum ProtectionDialogMode { Notify, Assist, Protect }

    /// <summary>
    /// Type of protection that triggered the dialog
    /// </summary>
    public enum ProtectionType { Command, Event, Rule, Pin }

    /// <summary>
    /// Action type for the confirmation dialog
    /// </summary>
    public enum ConfirmationActionType
    {
        Continue,   // Normal continue action (blue/primary)
        Delete,     // Delete action (red/danger)
        Modify,     // Modify action (orange/warning)
        Neutral     // Neutral action (gray)
    }

    /// <summary>
    /// ViewModel for Guide mode dialog with image support
    /// </summary>
    public class GuideDialogViewModel : ProtectionDialogBaseViewModel
    {
        private string _userComment;
        private bool _commentRequired;
        private bool _userAllowed;
        private BitmapImage _beforeImage;
        private BitmapImage _afterImage;
        private BitmapImage _previewImage;
        private string _beforeImagePath;
        private string _afterImagePath;
        private bool _showBeforeImage;
        private bool _showAfterImage;
        private bool _showImageSection;
        private ConfirmationActionType _actionType;
        private string _proceedButtonText;
        private string _cancelButtonText;
        private string _commandName;
        private string _commandDescription;
        private bool _isDeleteAction;
        private ProtectionDialogMode _dialogMode = ProtectionDialogMode.Assist;
        private ProtectionType _protectionType = ProtectionType.Command;
        private string _contentTitle = "";

        #region Properties

        public ProtectionDialogMode DialogMode
        {
            get => _dialogMode;
            set
            {
                SetProperty(ref _dialogMode, value);
                OnPropertyChanged(nameof(IsProtectMode));
                OnPropertyChanged(nameof(ShowOtpButton));
                OnPropertyChanged(nameof(ShowConfirmButton));
                OnPropertyChanged(nameof(MascotImageSource));
            }
        }

        public string MascotImageSource => _dialogMode switch
        {
            ProtectionDialogMode.Notify  => "pack://application:,,,/BIManageRevit;component/BIManage/Resources/Images/OctopusNotify.png",
            ProtectionDialogMode.Protect => "pack://application:,,,/BIManageRevit;component/BIManage/Resources/Images/OctopusProtect.png",
            _                            => "pack://application:,,,/BIManageRevit;component/BIManage/Resources/Images/OctopusAssist.png",
        };

        public ProtectionType ProtectionTypeValue
        {
            get => _protectionType;
            set => SetProperty(ref _protectionType, value);
        }

        public string ContentTitle
        {
            get => _contentTitle;
            set => SetProperty(ref _contentTitle, value);
        }

        public bool IsProtectMode => _dialogMode == ProtectionDialogMode.Protect;
        public bool ShowOtpButton => _dialogMode == ProtectionDialogMode.Protect;
        public bool ShowConfirmButton => _dialogMode == ProtectionDialogMode.Assist;

        public string UserComment
        {
            get => _userComment;
            set
            {
                SetProperty(ref _userComment, value);
                OnPropertyChanged(nameof(CommentHint));
                ((RelayCommand)ProceedCommand)?.NotifyCanExecuteChanged();
            }
        }

        /// <summary>
        /// Shows character count hint when comment is required (e.g. "4/10 characters")
        /// </summary>
        public string CommentHint
        {
            get
            {
                if (!CommentRequired) return "";
                int len = UserComment?.Trim().Length ?? 0;
                if (len >= MinCommentLength)
                    return $"{len} characters";
                return $"{len}/{MinCommentLength} characters (min {MinCommentLength})";
            }
        }

        public bool CommentRequired
        {
            get => _commentRequired;
            set => SetProperty(ref _commentRequired, value);
        }

        public bool UserAllowed
        {
            get => _userAllowed;
            private set => SetProperty(ref _userAllowed, value);
        }

        public BitmapImage BeforeImage
        {
            get => _beforeImage;
            set => SetProperty(ref _beforeImage, value);
        }

        public BitmapImage AfterImage
        {
            get => _afterImage;
            set => SetProperty(ref _afterImage, value);
        }

        /// <summary>
        /// Single preview image shown in the dialog (before screenshot or whichever is available)
        /// </summary>
        public BitmapImage PreviewImage
        {
            get => _previewImage;
            set => SetProperty(ref _previewImage, value);
        }

        public string BeforeImagePath
        {
            get => _beforeImagePath;
            set
            {
                SetProperty(ref _beforeImagePath, value);
                LoadBeforeImage();
            }
        }

        public string AfterImagePath
        {
            get => _afterImagePath;
            set
            {
                SetProperty(ref _afterImagePath, value);
                LoadAfterImage();
            }
        }

        public bool ShowBeforeImage
        {
            get => _showBeforeImage;
            set
            {
                SetProperty(ref _showBeforeImage, value);
                UpdateImageSectionVisibility();
            }
        }

        public bool ShowAfterImage
        {
            get => _showAfterImage;
            set
            {
                SetProperty(ref _showAfterImage, value);
                UpdateImageSectionVisibility();
            }
        }

        public bool ShowImageSection
        {
            get => _showImageSection;
            private set
            {
                SetProperty(ref _showImageSection, value);
                OnPropertyChanged(nameof(ShowNoImageText));
            }
        }

        /// <summary>
        /// True when no preview image is available (show placeholder text)
        /// </summary>
        public bool ShowNoImageText => !ShowImageSection;

        public ConfirmationActionType ActionType
        {
            get => _actionType;
            set
            {
                SetProperty(ref _actionType, value);
                UpdateActionTypeProperties();
            }
        }

        public string ProceedButtonText
        {
            get => _proceedButtonText;
            set => SetProperty(ref _proceedButtonText, value);
        }

        public string CancelButtonText
        {
            get => _cancelButtonText;
            set => SetProperty(ref _cancelButtonText, value);
        }

        public string CommandName
        {
            get => _commandName;
            set => SetProperty(ref _commandName, value);
        }

        public string CommandDescription
        {
            get => _commandDescription;
            set => SetProperty(ref _commandDescription, value);
        }

        public bool IsDeleteAction
        {
            get => _isDeleteAction;
            private set => SetProperty(ref _isDeleteAction, value);
        }

        // For button style binding
        public bool IsContinueAction => ActionType == ConfirmationActionType.Continue;
        public bool IsModifyAction => ActionType == ConfirmationActionType.Modify;
        public bool IsNeutralAction => ActionType == ConfirmationActionType.Neutral;

        #endregion

        #region Commands

        public ICommand ProceedCommand { get; }
        public ICommand CancelCommand { get; }

        #endregion

        public GuideDialogViewModel()
        {
            ProceedCommand = new RelayCommand(OnProceed, CanProceed);
            CancelCommand = new RelayCommand(OnCancel);

            // Default values
            ProceedButtonText = "Confirm";
            CancelButtonText = "Cancel";
            ActionType = ConfirmationActionType.Continue;
        }

        public override void Initialize(RuleEvaluationResult result, ICollection<ElementId> elements)
        {
            base.Initialize(result, elements);

            DialogTitle = "Action Requires Confirmation";
            // Leave empty when the rule has no Message configured — the dialog hides
            // the message panel via HasDialogMessage so users don't see a blank blue box.
            DialogMessage = result?.CombinedMessage ?? string.Empty;
            CommentRequired = result?.RequireComment ?? false;
            UserAllowed = false;

            // Determine action type based on command
            DetermineActionType(result);
        }

        /// <summary>
        /// Initialize with additional context
        /// </summary>
        public void Initialize(
            RuleEvaluationResult result,
            ICollection<ElementId> elements,
            string commandName,
            string commandDescription = null,
            string beforeImagePath = null,
            string afterImagePath = null)
        {
            Initialize(result, elements);

            CommandName = commandName;
            CommandDescription = commandDescription;

            // Set images if provided
            if (!string.IsNullOrEmpty(beforeImagePath))
            {
                ShowBeforeImage = true;
                BeforeImagePath = beforeImagePath;
            }

            if (!string.IsNullOrEmpty(afterImagePath))
            {
                ShowAfterImage = true;
                AfterImagePath = afterImagePath;
            }

            // Update dialog title with command name
            if (!string.IsNullOrEmpty(commandName))
            {
                DialogTitle = $"Confirm {commandName}";
            }
        }

        /// <summary>
        /// Set the before screenshot image directly
        /// </summary>
        public void SetBeforeImage(BitmapImage image)
        {
            BeforeImage = image;
            ShowBeforeImage = image != null;
            // Use as preview image (before takes priority)
            if (image != null)
                PreviewImage = image;
        }

        /// <summary>
        /// Set the after screenshot image directly
        /// </summary>
        public void SetAfterImage(BitmapImage image)
        {
            AfterImage = image;
            ShowAfterImage = image != null;
            // Use as preview if no before image
            if (image != null && PreviewImage == null)
                PreviewImage = image;
        }

        /// <summary>
        /// Set the single preview image directly
        /// </summary>
        public void SetPreviewImage(BitmapImage image)
        {
            PreviewImage = image;
            ShowImageSection = image != null;
        }

        private void DetermineActionType(RuleEvaluationResult result)
        {
            // Check if this is a delete/remove operation
            var isDelete = CommandName?.ToLowerInvariant()?.Contains("delete") == true ||
                          CommandName?.ToLowerInvariant()?.Contains("remove") == true ||
                          CommandName?.ToLowerInvariant()?.Contains("demolish") == true ||
                          CommandName?.ToLowerInvariant()?.Contains("unpin") == true;

            if (isDelete)
            {
                ActionType = ConfirmationActionType.Delete;
                return;
            }

            // Check if this is a modify operation
            var isModify = CommandName?.ToLowerInvariant()?.Contains("modify") == true ||
                          CommandName?.ToLowerInvariant()?.Contains("edit") == true ||
                          CommandName?.ToLowerInvariant()?.Contains("change") == true ||
                          CommandName?.ToLowerInvariant()?.Contains("move") == true ||
                          CommandName?.ToLowerInvariant()?.Contains("rotate") == true;

            if (isModify)
            {
                ActionType = ConfirmationActionType.Modify;
                return;
            }

            // Default to continue
            ActionType = ConfirmationActionType.Continue;
        }

        private void UpdateActionTypeProperties()
        {
            IsDeleteAction = ActionType == ConfirmationActionType.Delete;
            OnPropertyChanged(nameof(IsContinueAction));
            OnPropertyChanged(nameof(IsModifyAction));
            OnPropertyChanged(nameof(IsNeutralAction));

            ProceedButtonText = "Confirm";
            CancelButtonText = "Cancel";
        }

        private void UpdateImageSectionVisibility()
        {
            ShowImageSection = ShowBeforeImage || ShowAfterImage;
        }

        private void LoadBeforeImage()
        {
            if (string.IsNullOrEmpty(BeforeImagePath))
            {
                BeforeImage = null;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(BeforeImagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                BeforeImage = bitmap;
                PreviewImage = bitmap;
            }
            catch
            {
                BeforeImage = null;
            }
        }

        private void LoadAfterImage()
        {
            if (string.IsNullOrEmpty(AfterImagePath))
            {
                AfterImage = null;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(AfterImagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                AfterImage = bitmap;
            }
            catch
            {
                AfterImage = null;
            }
        }

        private const int MinCommentLength = 10;

        private bool CanProceed()
        {
            if (CommentRequired && (UserComment?.Trim().Length ?? 0) < MinCommentLength)
                return false;

            return true;
        }

        public void OnProceed()
        {
            UserAllowed = true;
        }

        public void OnCancel()
        {
            UserAllowed = false;
        }
    }
}
