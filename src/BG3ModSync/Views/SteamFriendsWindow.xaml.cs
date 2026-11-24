using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BG3ModSync.Services;

namespace BG3ModSync.Views;

public partial class SteamFriendsWindow : Window
{
    private readonly SteamP2PRelayService _steamService;
    private readonly Action<string> _logCallback;
    private readonly DispatcherTimer _autoRefreshTimer;
    private bool _isRefreshing;
    public string? SelectedHostTarget { get; private set; }

    public SteamFriendsWindow(SteamP2PRelayService steamService, Action<string> logCallback)
    {
        InitializeComponent();
        _steamService = steamService;
        _logCallback = logCallback;

        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshSteamStateAsync();
        Loaded += async (_, _) =>
        {
            await RefreshSteamStateAsync();
            _autoRefreshTimer.Start();
        };
        Closed += (_, _) => _autoRefreshTimer.Stop();
    }

    private async Task RefreshSteamStateAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            string appName = SteamP2PRelayService.GetAppIdDisplayName(_steamService.ActiveAppId);
            SteamAppHeaderText.Text = $"SELECT HOST — {appName.ToUpperInvariant()}";
            if (_steamService.TryInitialize(_logCallback))
            {
                ulong? prevSelectedSteamId = (FriendsListBox.SelectedItem as SteamFriendInfo)?.SteamId;
                var matchingFriends = await _steamService.GetOnlineFriendsAsync();
                SteamStatusText.Text = $"Signed in as {_steamService.LocalPersonaName}  •  Showing friends active in {appName} or hosting a Lobby";
                FriendsCountHeaderText.Text = $"Friends in {appName} ({matchingFriends.Count})";
                FriendsListBox.ItemsSource = matchingFriends;
                if (prevSelectedSteamId.HasValue)
                {
                    FriendsListBox.SelectedItem = matchingFriends.FirstOrDefault(f => f.SteamId == prevSelectedSteamId.Value);
                }
            }
            else
            {
                SteamStatusText.Text = $"Steam Client is not running or could not initialize {appName}.";
                FriendsCountHeaderText.Text = "Friends (0)";
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async void RefreshFriendsBtn_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSteamStateAsync();
    }

    private void FriendsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FriendsListBox.SelectedItem is SteamFriendInfo)
        {
            UseFriendAsHostBtn_Click(sender, e);
        }
    }

    private async void UseFriendAsHostBtn_Click(object sender, RoutedEventArgs e)
    {
        if (FriendsListBox.SelectedItem is not SteamFriendInfo friend)
        {
            MessageBox.Show(this, "Select an online friend running the same Game / AppID first.", "Select Host", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ulong lobbyId = friend.LobbyId;
        if (lobbyId == 0 && friend.SteamId != 0)
        {
            // One live Steam Matchmaking LobbyList lookup in case Host started hosting right before selection
            var found = await _steamService.QueryActiveModSyncLobbiesAsync(friend.SteamId, 1200);
            if (found.TryGetValue(friend.SteamId, out ulong liveLid) && liveLid != 0)
            {
                lobbyId = liveLid;
            }
        }

        ulong targetId = lobbyId != 0 ? lobbyId : friend.SteamId;
        SelectedHostTarget = SteamP2PRelayService.EncodeRoomCode(targetId);
        _logCallback($"[STEAM] Selected Host '{friend.Name}' -> {SelectedHostTarget}");
        DialogResult = true;
        Close();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
