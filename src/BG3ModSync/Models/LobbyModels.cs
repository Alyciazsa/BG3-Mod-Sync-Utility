using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace BG3ModSync.Models;

public class ClientLobbyTelemetry
{
    public string ClientKey { get; set; } = "";
    public ulong SteamId { get; set; }
    public string Name { get; set; } = "";
    public string IpAndPort { get; set; } = "";
    public int PingMs { get; set; } = 1;
    public double SpeedBytesPerSec { get; set; }
    public double ProgressPercent { get; set; }
    public string StageText { get; set; } = "Connected";
    public bool IsSteamMode { get; set; }
    public bool IsCgnat { get; set; }
}

public class LobbyClientViewModel : INotifyPropertyChanged
{
    private string _clientKey = "";
    private ulong _steamId;
    private string _name = "Player";
    private string _idDisplay = "";
    private string _ipPortDisplay = "";
    private string _pingDisplay = "Ping: --";
    private string _speedDisplay = "0 B/s";
    private double _progressPercent;
    private string _stageText = "Connected — Handshaked";
    private ImageSource? _avatarSource;
    private bool _isSteamMode;
    private bool _isSelf;
    private bool _isCgnat;

    public string ClientKey
    {
        get => _clientKey;
        set { _clientKey = value; OnPropertyChanged(); }
    }

    public ulong SteamId
    {
        get => _steamId;
        set
        {
            if (_steamId != value)
            {
                _steamId = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IdVisibility));
            }
        }
    }

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    public string IdDisplay
    {
        get => _idDisplay;
        set
        {
            if (_idDisplay != value)
            {
                _idDisplay = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IdVisibility));
            }
        }
    }

    public Visibility IdVisibility => !string.IsNullOrWhiteSpace(_idDisplay) ? Visibility.Visible : Visibility.Collapsed;

    public string IpPortDisplay
    {
        get => _ipPortDisplay;
        set { if (_ipPortDisplay != value) { _ipPortDisplay = value; OnPropertyChanged(); } }
    }

    public string PingDisplay
    {
        get => _pingDisplay;
        set { if (_pingDisplay != value) { _pingDisplay = value; OnPropertyChanged(); } }
    }

    public string SpeedDisplay
    {
        get => _speedDisplay;
        set { if (_speedDisplay != value) { _speedDisplay = value; OnPropertyChanged(); } }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            double clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(_progressPercent - clamped) > 0.05)
            {
                _progressPercent = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercentText));
            }
        }
    }

    public string ProgressPercentText => $"{_progressPercent:F0}%";

    public string StageText
    {
        get => _stageText;
        set { if (_stageText != value) { _stageText = value; OnPropertyChanged(); } }
    }

    public ImageSource? AvatarSource
    {
        get => _avatarSource;
        set
        {
            if (!ReferenceEquals(_avatarSource, value))
            {
                _avatarSource = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AvatarVisibility));
                OnPropertyChanged(nameof(FallbackIconVisibility));
            }
        }
    }

    public Visibility AvatarVisibility => _avatarSource != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FallbackIconVisibility => _avatarSource == null ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSteamMode
    {
        get => _isSteamMode;
        set { if (_isSteamMode != value) { _isSteamMode = value; OnPropertyChanged(); } }
    }

    public bool IsSelf
    {
        get => _isSelf;
        set
        {
            if (_isSelf != value)
            {
                _isSelf = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatsVisibility));
            }
        }
    }

    public Visibility StatsVisibility => _isSelf ? Visibility.Collapsed : Visibility.Visible;

    public bool IsCgnat
    {
        get => _isCgnat;
        set
        {
            if (_isCgnat != value)
            {
                _isCgnat = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CgnatVisibility));
            }
        }
    }

    public Visibility CgnatVisibility => _isCgnat ? Visibility.Visible : Visibility.Collapsed;

    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0.1) return "0.0 MB/s";
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / (1024.0 * 1024.0):0.0} MB/s";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024.0:0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
