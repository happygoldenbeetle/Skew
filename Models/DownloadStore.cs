using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Skew.Models;

public partial class DownloadStore : ObservableObject
{
    public static DownloadStore Shared { get; } = new();

    public ObservableCollection<DownloadItem> Items { get; } = new();

    [ObservableProperty]
    private int _activityToken;

    public bool HasActiveDownloads => Items.Any(i => i.IsInProgress && !i.IsComplete && !i.IsCanceled);
    
    public double TotalPercent 
    {
        get
        {
            var active = Items.Where(i => i.IsInProgress && !i.IsComplete && !i.IsCanceled).ToList();
            if (active.Count == 0) return 0;
            return active.Average(i => i.FractionComplete) * 100.0;
        }
    }
    
    public bool HasItems => Items.Count > 0;
    
    public bool IsEmpty => Items.Count == 0;
    
    public bool HasClearableDownloads => Items.Any(i => i.IsComplete || i.IsCanceled);

    private DownloadStore() 
    {
        Items.CollectionChanged += (s, e) => 
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasClearableDownloads));
            OnPropertyChanged(nameof(HasActiveDownloads));
            OnPropertyChanged(nameof(TotalPercent));
        };
    }

    public void Ingest(uint id, string url, string filename, string path, long received, long total, int percent, long speed, bool isComplete, bool isCanceled, bool isInProgress)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (item != null)
        {
            item.Url = url;
            item.Filename = filename;
            item.Path = path;
            item.Received = received;
            item.Total = total;
            item.Percent = percent;
            item.Speed = speed;
            item.IsComplete = isComplete;
            item.IsCanceled = isCanceled;
            item.IsInProgress = isInProgress;
            
            OnPropertyChanged(nameof(HasActiveDownloads));
            OnPropertyChanged(nameof(TotalPercent));
            OnPropertyChanged(nameof(HasClearableDownloads));
        }
        else
        {
            item = new DownloadItem
            {
                Id = id,
                Url = url,
                Filename = filename,
                Path = path,
                Received = received,
                Total = total,
                Percent = percent,
                Speed = speed,
                IsComplete = isComplete,
                IsCanceled = isCanceled,
                IsInProgress = isInProgress
            };
            Items.Insert(0, item);
            ActivityToken++;
        }
    }

    public void Reveal(DownloadItem item)
    {
        if (string.IsNullOrEmpty(item.Path)) return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
        }
        catch { }
    }

    public void Open(DownloadItem item)
    {
        if (!item.IsComplete || string.IsNullOrEmpty(item.Path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch { }
    }

    public void ShowDefaultFolder()
    {
        try
        {
            string downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string target = System.IO.Path.Combine(downloads, "Downloads");
            System.Diagnostics.Process.Start("explorer.exe", $"\"{target}\"");
        }
        catch { }
    }

    public void ClearFinished()
    {
        var erased = Items.Where(i => i.IsComplete || i.IsCanceled).ToList();
        foreach (var i in erased)
        {
            Items.Remove(i);
        }
        OnPropertyChanged(nameof(HasActiveDownloads));
    }

    public void Cancel(DownloadItem item)
    {
        if (item.IsInProgress && !item.IsComplete && !item.IsCanceled)
        {
            Skew.Cef.BrowserClient.CancelDownload(item.Id);
        }
    }
}
