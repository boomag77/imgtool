using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ImgViewer.Models
{
    public sealed class BatchViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<BatchTaskItem> Tasks { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Clear()
        {
            Tasks.Clear();
            OnPropertyChanged(nameof(Tasks));
        }

        public BatchTaskItem AddPending(string id, string displayName)
        {
            var existing = Find(id);
            if (existing != null)
                return existing;

            var item = new BatchTaskItem(id, displayName, "pending", 0);
            Tasks.Add(item);
            return item;
        }

        public void Remove(string id)
        {
            var item = Find(id);
            if (item != null)
            {
                Tasks.Remove(item);
            }
        }

        public void SetInProgress(string id)
        {
            var item = Find(id);
            if (item == null) return;
            item.Status = "in progress";
            item.Progress = 0;
        }

        public void SetProgress(string id, int percent)
        {
            var item = Find(id);
            if (item == null) return;
            item.Progress = Math.Max(0, Math.Min(100, percent));
        }

        private BatchTaskItem? Find(string id)
        {
            return Tasks.FirstOrDefault(t => t.Id == id);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class BatchTaskItem : INotifyPropertyChanged
    {
        private string _status;
        private int _progress;

        public BatchTaskItem(string id, string displayName, string status, int progress)
        {
            Id = id;
            DisplayName = displayName;
            _status = status;
            _progress = progress;
        }

        public string Id { get; }
        public string DisplayName { get; }

        public string Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsInProgress));
            }
        }

        public int Progress
        {
            get => _progress;
            set
            {
                if (_progress == value) return;
                _progress = value;
                OnPropertyChanged();
            }
        }

        public bool IsInProgress => string.Equals(Status, "in progress", StringComparison.OrdinalIgnoreCase);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
