using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mori.Models;

public partial class DownloadItem : ObservableObject
{
    [ObservableProperty] private uint _id;
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _filename = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private long _received;
    [ObservableProperty] private long _total;
    [ObservableProperty] private int _percent = -1;
    [ObservableProperty] private long _speed;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _isCanceled;
    [ObservableProperty] private bool _isInProgress;

    public double FractionComplete
    {
        get
        {
            if (Percent >= 0) return Percent / 100.0;
            if (Total > 0) return (double)Received / Total;
            return 0;
        }
    }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Filename)) return Filename;
            if (!string.IsNullOrEmpty(Path)) return System.IO.Path.GetFileName(Path);
            return "Download";
        }
    }

    public string SizeSummary
    {
        get
        {
            string recv = FormatBytes(Received);
            if (Total > 0)
            {
                string tot = FormatBytes(Total);
                return $"{recv} of {tot}";
            }
            return recv;
        }
    }

    public string StatusText
    {
        get
        {
            if (IsComplete) return "Completed";
            if (IsCanceled) return "Canceled";
            
            if (Speed > 0 && Total > 0 && Received < Total)
            {
                long remainingBytes = Total - Received;
                double secondsLeft = (double)remainingBytes / Speed;
                TimeSpan eta = TimeSpan.FromSeconds(secondsLeft);
                
                string etaStr;
                if (eta.TotalHours >= 1)
                    etaStr = $"{(int)eta.TotalHours}h {eta.Minutes}m";
                else if (eta.TotalMinutes >= 1)
                    etaStr = $"{eta.Minutes}m {eta.Seconds}s";
                else
                    etaStr = $"{eta.Seconds}s";
                    
                return $"{SizeSummary} — {etaStr} left";
            }
            else if (Speed > 0)
            {
                string rate = FormatBytes(Speed);
                return $"{SizeSummary} — {rate}/s";
            }
            return SizeSummary;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] suf = { "B", "KB", "MB", "GB", "TB" };
        if (bytes == 0) return "0 B";
        long bytesAbs = Math.Abs(bytes);
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytesAbs, 1024)));
        place = Math.Min(place, suf.Length - 1);
        double num = Math.Round(bytesAbs / Math.Pow(1024, place), 1);
        return (Math.Sign(bytes) * num).ToString() + " " + suf[place];
    }

    private Microsoft.UI.Xaml.Media.ImageSource? _fileIcon;
    public Microsoft.UI.Xaml.Media.ImageSource? FileIcon
    {
        get => _fileIcon;
        private set 
        {
            if (SetProperty(ref _fileIcon, value))
            {
                OnPropertyChanged(nameof(HasFileIcon));
                OnPropertyChanged(nameof(HasNoFileIcon));
            }
        }
    }
    
    public bool HasFileIcon => _fileIcon != null;
    public bool HasNoFileIcon => _fileIcon == null;

    public string IconGlyph => IsCanceled ? "\uE711" : (IsComplete ? "\uE8A5" : "\uE896");
    
    public bool IsProgressVisible => (IsInProgress && !IsComplete && !IsCanceled);
    
    public bool IsCancelVisible => (IsInProgress && !IsComplete && !IsCanceled);
    
    public bool IsRevealVisible => IsComplete;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Percent) || e.PropertyName == nameof(Received) || e.PropertyName == nameof(Total))
        {
            OnPropertyChanged(nameof(FractionComplete));
            OnPropertyChanged(nameof(SizeSummary));
            OnPropertyChanged(nameof(StatusText));
        }
        else if (e.PropertyName == nameof(Speed) || e.PropertyName == nameof(IsComplete) || e.PropertyName == nameof(IsCanceled) || e.PropertyName == nameof(IsInProgress))
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IconGlyph));
            OnPropertyChanged(nameof(IsProgressVisible));
            OnPropertyChanged(nameof(IsCancelVisible));
            OnPropertyChanged(nameof(IsRevealVisible));
            if (e.PropertyName == nameof(IsComplete) && IsComplete)
            {
                LoadIconAsync();
            }
        }
        else if (e.PropertyName == nameof(Filename) || e.PropertyName == nameof(Path))
        {
            OnPropertyChanged(nameof(DisplayName));
            LoadIconAsync();
        }
    }

    private async void LoadIconAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(Path)) return;
            
            // Wait a tiny bit to ensure the file exists on disk if it was just created
            await System.Threading.Tasks.Task.Delay(50);
            
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path);
            var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.ListView, 32, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
            if (thumb != null)
            {
                // Must be invoked on UI thread if this is a background task, but PropertyChanged is usually marshalled or this is already UI thread.
                // To be safe, we use DispatcherQueue but let's assume UI thread for now as properties are set on UI thread via Ingest.
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bitmap.SetSourceAsync(thumb);
                FileIcon = bitmap;
            }
        }
        catch { }
    }
}
