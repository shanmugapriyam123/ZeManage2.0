using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BIManage.Models.AI
{
    /// <summary>
    /// Represents a single chat message displayed in the UI.
    /// Implements INotifyPropertyChanged for streaming updates and feedback state.
    /// </summary>
    public class ChatMessage : INotifyPropertyChanged
    {
        private string _text = string.Empty;
        private int? _feedbackRating;

        /// <summary>Database ID for this message (set after persistence). Used for feedback tracking.</summary>
        public string? ChatMessageId { get; set; }

        public string Text
        {
            get => _text;
            set { _text = value; OnPropertyChanged(); }
        }

        public bool IsUser { get; set; }
        public DateTime Timestamp { get; set; }

        private List<string> _followUpQuestions = new List<string>();
        public List<string> FollowUpQuestions
        {
            get => _followUpQuestions;
            set { _followUpQuestions = value; OnPropertyChanged(); }
        }

        /// <summary>Feedback rating: null=none, 1=thumbs up, -1=thumbs down.</summary>
        public int? FeedbackRating
        {
            get => _feedbackRating;
            set { _feedbackRating = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
