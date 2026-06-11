using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mori.Models;

public partial class DownloadItem : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty]
    private string _fileName = "";

    [ObservableProperty]
    private string _url = "";

    [ObservableProperty]
    private double _progress = 0.0;

    [ObservableProperty]
    private bool _isCompleted = false;

    [ObservableProperty]
    private bool _isFailed = false;
}

public partial class DownloadStore : ObservableObject
{
    public static DownloadStore Shared { get; } = new();

    public ObservableCollection<DownloadItem> Downloads { get; } = new();

    private DownloadStore() { }
}
