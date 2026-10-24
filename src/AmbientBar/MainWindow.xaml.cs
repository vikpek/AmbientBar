using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Forms = System.Windows.Forms;

namespace AmbientBar;

public partial class MainWindow : Window
{
    readonly MediaPlayer _player = new() { AudioCategory = MediaPlayerAudioCategory.Media };
    readonly ObservableCollection<Track> _queue = new();
    readonly ObservableCollection<Track> _results = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly AppState _state = AppState.Load();
    readonly Forms.NotifyIcon _tray = new();

    Track? _current;
    int _playToken;          // bumps on every new PlayAsync so stale resolves are dropped
    double _seekOnOpen;      // seconds to jump to once the stream opens (resume / recovery)
    double _lastPosition;
    int _retries;
    CancellationTokenSource? _searchCts;

    public MainWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _results;
        QueueList.ItemsSource = _queue;

        foreach (var t in _state.Queue) _queue.Add(t);
        _current = _queue.FirstOrDefault(t => t.Id == _state.CurrentId);
        if (_current != null)
        {
            _current.IsCurrent = true;
            _lastPosition = _state.Position;
            NowPlaying.Text = _current.Title;
            UpdateTime();
        }
        _queue.CollectionChanged += (_, _) => { UpdateQueueEmpty(); SaveState(); };
        UpdateQueueEmpty();
        UpdateRepeat();

        _player.Volume = _state.Volume;
        // Media keys / the Windows media flyout drive play & pause directly; we only add "next".
        _player.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        _player.CommandManager.NextReceived += (_, e) => { e.Handled = true; Dispatcher.Invoke(() => Next(manual: true)); };
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => Dispatcher.Invoke(UpdatePlayButton);
        _player.MediaOpened += (_, _) =>
        {
            if (_seekOnOpen > 0) _player.PlaybackSession.Position = TimeSpan.FromSeconds(_seekOnOpen);
            _seekOnOpen = 0;
        };
        _player.MediaEnded += (_, _) => Dispatcher.Invoke(() => Next(manual: false));
        _player.MediaFailed += (_, e) => Dispatcher.Invoke(() => OnMediaFailed(e.ErrorMessage));

        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); NowPlaying.Text = _current?.Title ?? "Nothing playing"; };

        SetupTray();
        Loaded += (_, _) => Dock();
        SizeChanged += (_, _) => Dock();
        Deactivated += (_, _) => SetExpanded(false);
        Closing += (_, _) => { SaveState(); _tray.Visible = false; _tray.Dispose(); _player.Dispose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) SetExpanded(false); };
    }

    // ---------- playback ----------

    async Task PlayAsync(Track track, double startAt = 0)
    {
        var token = ++_playToken;
        SetCurrent(track);
        _seekOnOpen = startAt;
        _lastPosition = startAt;
        NowPlaying.Text = "Loading… " + track.Title;

        string url;
        try
        {
            url = await YtDlp.ResolveAudioUrlAsync(track.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (token == _playToken) ShowStatus("Couldn't load: " + ex.Message, sticky: true);
            return;
        }
        if (token != _playToken) return;

        var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(url)));
        var props = item.GetDisplayProperties();
        props.Type = MediaPlaybackType.Music;
        props.MusicProperties.Title = track.Title;
        props.MusicProperties.Artist = track.Channel ?? "YouTube";
        props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri($"https://i.ytimg.com/vi/{track.Id}/hqdefault.jpg"));
        item.ApplyDisplayProperties(props);

        _player.Source = item;
        _player.Play();
        NowPlaying.Text = track.Title;
    }

    void TogglePlay()
    {
        if (_current == null)
        {
            if (_queue.Count > 0) _ = PlayAsync(_queue[0]);
            else SetExpanded(true);
            return;
        }
        if (_player.Source == null)
        {
            _ = PlayAsync(_current, _lastPosition);
            return;
        }
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
        else _player.Play();
    }

    void Next(bool manual)
    {
        if (_queue.Count == 0) return;
        int i = _current == null ? -1 : _queue.IndexOf(_current);
        if (i + 1 < _queue.Count) _ = PlayAsync(_queue[i + 1]);
        else if (_state.Repeat || manual) _ = PlayAsync(_queue[0]);
        else Stop();
    }

    void Stop()
    {
        _playToken++;
        _player.Pause();
        _player.Source = null;
        _lastPosition = 0;
        UpdatePlayButton();
        UpdateTime();
    }

    void OnMediaFailed(string message)
    {
        // Stream URLs expire after a few hours, which matters for 10h tracks: re-resolve and continue.
        if (_current != null && _retries < 3)
        {
            _retries++;
            _ = PlayAsync(_current, _lastPosition);
        }
        else
        {
            ShowStatus("Playback failed: " + message, sticky: true);
        }
    }

    void OnTick()
    {
        if (_player.Source != null && _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            var pos = _player.PlaybackSession.Position.TotalSeconds;
            if (pos > _lastPosition + 60) _retries = 0; // a minute of healthy playback resets the retry budget
            if (pos > 0) _lastPosition = pos;
            if ((int)pos % 30 == 0) SaveState();
        }
        UpdateTime();
    }

    void SetCurrent(Track? track)
    {
        if (_current != null) _current.IsCurrent = false;
        _current = track;
        if (track != null) track.IsCurrent = true;
        SaveState();
    }

    // ---------- UI ----------

    void UpdatePlayButton()
    {
        bool playing = _player.PlaybackSession.PlaybackState is MediaPlaybackState.Playing
            or MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
        PlayButton.Content = playing ? "" : "";
        PlayButton.ToolTip = playing ? "Pause" : "Play";
    }

    void UpdateTime()
    {
        if (_current == null) { TimeText.Text = ""; return; }
        var total = _current.Duration ?? _player.PlaybackSession.NaturalDuration.TotalSeconds;
        TimeText.Text = total > 0
            ? $"{Track.FormatTime(_lastPosition)} / {Track.FormatTime(total)}"
            : Track.FormatTime(_lastPosition);
    }

    void UpdateQueueEmpty() =>
        QueueEmpty.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void UpdateRepeat()
    {
        RepeatButton.Foreground = _state.Repeat ? (System.Windows.Media.Brush)FindResource("Accent") : (System.Windows.Media.Brush)FindResource("Dim");
        RepeatButton.ToolTip = _state.Repeat ? "Repeat queue: on" : "Repeat queue: off";
    }

    void ShowStatus(string text, bool sticky = false)
    {
        NowPlaying.Text = text;
        _statusTimer.Stop();
        if (!sticky) _statusTimer.Start();
    }

    void SetExpanded(bool expanded)
    {
        Panel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Content = expanded ? "" : "";
        if (expanded)
        {
            Activate();
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    void Dock()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Bottom - ActualHeight - 12;
    }

    void SaveState()
    {
        _state.Queue = _queue.ToList();
        _state.CurrentId = _current?.Id;
        _state.Position = _lastPosition;
        _state.Volume = _player.Volume;
        _state.Save();
    }

    void SetupTray()
    {
        _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _tray.Text = $"AmbientBar {Updates.Current.ToString(3)}";
        _tray.Visible = true;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            if (IsVisible) Hide();
            else { Show(); Activate(); }
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show / hide", null, (_, _) => { if (IsVisible) Hide(); else Show(); });
        menu.Items.Add("Play / pause", null, (_, _) => TogglePlay());
        menu.Items.Add("Next", null, (_, _) => Next(manual: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
        _ = CheckForUpdateAsync(menu);
    }

    async Task CheckForUpdateAsync(Forms.ContextMenuStrip menu)
    {
        var latest = await Updates.GetNewerReleaseAsync();
        if (latest == null) return;
        var item = new Forms.ToolStripMenuItem($"Update available: {latest.Tag}", null, (_, _) => Updates.OpenDownloadPage());
        item.Font = new System.Drawing.Font(item.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Insert(0, item);
        menu.Items.Insert(1, new Forms.ToolStripSeparator());
        _tray.Text = $"AmbientBar — {latest.Tag} available";
        ShowStatus($"AmbientBar {latest.Tag} is available — see the tray menu");
    }

    // ---------- search ----------

    async Task SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();

        _results.Clear();
        ResultsList.Visibility = Visibility.Collapsed;
        ResultsStatus.Text = "Searching…";
        ResultsStatus.Visibility = Visibility.Visible;
        try
        {
            var found = await YtDlp.SearchAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var t in found) _results.Add(t);
            ResultsStatus.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultsStatus.Text = "No results.";
            ResultsList.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) ResultsStatus.Text = "Search failed: " + ex.Message;
        }
    }

    // ---------- event handlers ----------

    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void Next_Click(object sender, RoutedEventArgs e) => Next(manual: true);
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Expand_Click(object sender, RoutedEventArgs e) => SetExpanded(Panel.Visibility != Visibility.Visible);
    void NowPlaying_Click(object sender, MouseButtonEventArgs e) => SetExpanded(Panel.Visibility != Visibility.Visible);

    void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _state.Repeat = !_state.Repeat;
        UpdateRepeat();
        SaveState();
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchAsync(SearchBox.Text);
    }

    void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void ResultsList_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Track t) return;
        var existing = _queue.FirstOrDefault(q => q.Id == t.Id);
        if (existing == null)
        {
            _queue.Add(t);
            existing = t;
        }
        bool idle = _player.Source == null || _player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing;
        if (idle) _ = PlayAsync(existing);
        else ShowStatus("Queued: " + t.Title);
    }

    void QueueList_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track t) _ = PlayAsync(t);
    }

    void RemoveFromQueue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Track t) return;
        if (t == _current)
        {
            bool wasPlaying = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            int i = _queue.IndexOf(t);
            _queue.Remove(t);
            SetCurrent(null);
            Stop();
            NowPlaying.Text = "Nothing playing";
            if (wasPlaying && _queue.Count > 0) _ = PlayAsync(_queue[Math.Min(i, _queue.Count - 1)]);
        }
        else
        {
            _queue.Remove(t);
        }
    }

    void Bar_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _player.Volume = Math.Clamp(_player.Volume + (e.Delta > 0 ? 0.05 : -0.05), 0, 1);
        ShowStatus($"Volume {Math.Round(_player.Volume * 100)}%");
        SaveState();
    }
}
