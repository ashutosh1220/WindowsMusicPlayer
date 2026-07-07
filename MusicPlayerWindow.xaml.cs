using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Search;
using Windows.Storage.Streams;
using Windows.UI;

namespace WinUIMusicPlayer
{
    public class AppSettings
    {
        public double Volume { get; set; } = 0.7;
        public bool IsMuted { get; set; } = false;
        public double PlaybackRate { get; set; } = 1.0;
        public bool IsShuffled { get; set; } = false;
        public RepeatMode RepeatMode { get; set; } = RepeatMode.All;
        public string? LastSongPath { get; set; } = null;
        public double LastSeekPercent { get; set; } = 0.0;
        public int WindowX { get; set; } = 100;
        public int WindowY { get; set; } = 100;
        public int WindowWidth { get; set; } = 700;
        public int WindowHeight { get; set; } = 350;
    }

    public static class SettingsManager
    {
        private static readonly string _folder =
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrystalPlayer");

        private static readonly string _path =
            System.IO.Path.Combine(_folder, "settings.json");

        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static AppSettings Load()
        {
            try
            {
                if (System.IO.File.Exists(_path))
                {
                    var json = System.IO.File.ReadAllText(_path);
                    return JsonSerializer.Deserialize<AppSettings>(json, _opts) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Settings load error: {ex.Message}");
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_folder);
                System.IO.File.WriteAllText(_path, JsonSerializer.Serialize(settings, _opts));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Settings save error: {ex.Message}");
            }
        }
    }

    public sealed partial class MusicPlayerWindow : Window
    {
        private readonly ObservableCollection<Song> _allSongs = new();
        private ObservableCollection<Song> _filteredSongs = new();
        private Song? _currentSong;
        private bool _isPlaying;
        private bool _isMuted;
        private double _currentVolume = 0.7;
        private double _playbackRate = 1.0;
        private RepeatMode _repeatMode = RepeatMode.All;
        private bool _isShuffled;

        private List<Song> _shuffledOrder = new();
        private int _shuffleIndex = -1;

        private readonly Random _random = new();
        private bool _isInitialized;
        private bool _isSeeking;

        private readonly MediaPlayer _mediaPlayer = new();

        private readonly DispatcherTimer _progressTimer = new();
        private readonly DispatcherTimer _rippleTimer = new();
        private readonly DispatcherTimer _glowTimer = new();
        private readonly DispatcherTimer _autoSaveTimer = new();
        private DispatcherTimer? _playingAnimTimer = null;
        private DispatcherTimer? _playBtnAnimTimer = null;

        private double _pulse1R, _pulse1A;
        private double _pulse2R, _pulse2A;
        private double _pulse3R, _pulse3A;
        private double _glowPhase;
        private double _artRadius = 110.0;

        private Border? _currentPlayingBorder = null;
        private Storyboard? _playingStoryboard = null;
        private double MaxRippleR => _artRadius + 85.0;

        private Ellipse? _ring1, _ring2, _ring3;
        private Ellipse? _glowEllipse;

        private AppWindow? _appWindow;

        private const double BreakCompact = 380;
        private const double BreakMedium = 520;
        private double _lastWindowWidth = 700;

        private Windows.Graphics.PointInt32 _normalWindowPosition = new(100, 100);
        private OverlappedPresenterState? _lastPresenterState;

        private BitmapImage? _currentAlbumBitmap;
        private RandomAccessStreamReference? _currentThumbnailStreamRef;
        private AppSettings _settings = new();

        private SystemMediaTransportControls? _smtc;
        private MemoryStream? _albumArtStream = null;
        private IRandomAccessStream? _fallbackStream = null;
        private MemoryStream? _smtcStream = null;
        private string? _smtcThumbnailPath = null;
        public MusicPlayerWindow()
        {
            try
            {
                this.InitializeComponent();

                _settings = SettingsManager.Load();
                ApplyLoadedSettings();

                try
                {
                    var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                    _appWindow = AppWindow.GetFromWindowId(winId);

                    if (_appWindow != null)
                    {
                        _appWindow.SetIcon("Assets/Square44x44Logo.scale-200.ico");
                        var presenter = _appWindow.Presenter as OverlappedPresenter;
                        if (presenter != null)
                        {
                            presenter.IsMaximizable = true;
                            presenter.IsResizable = false;
                            _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                                _settings.WindowX, _settings.WindowY,
                                _settings.WindowWidth, _settings.WindowHeight));
                            BtnOpenSidebar.Visibility = Visibility.Collapsed;
                            _lastPresenterState = presenter.State;
                        }
                        _appWindow.Changed += AppWindow_Changed;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Window initialization error: {ex.Message}");
                }

                ExtendsContentIntoTitleBar = true;
                SetTitleBar(AppTitleBar);

                if (_appWindow != null)
                {
                    var titleBar = _appWindow.TitleBar;
                    titleBar.ButtonForegroundColor = Colors.White;
                    titleBar.ButtonHoverForegroundColor = Colors.White;
                    titleBar.ButtonPressedForegroundColor = Colors.LightGray;
                    titleBar.ButtonBackgroundColor = Colors.Transparent;
                    titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
                    titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
                }

                SetupMediaPlayer();
                InitSMTC();

                InitializeSampleData();
                SetupTimers();
                SetupRippleElements();
                UpdateVolumeUI();
                ApplyRepeatUI();
                ApplyShuffleUI();

                _isInitialized = true;

                DispatcherQueue.TryEnqueue(() => TryResumeLastSong());
                _mediaPlayer.Pause();
                PlayPauseIcon.Glyph = "\uE768";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Constructor error: {ex.Message}");
                ShowTemporaryStatus("Startup error: " + ex.Message);
            }
        }

        private void ApplyLoadedSettings()
        {
            _currentVolume = _settings.Volume;
            _isMuted = _settings.IsMuted;
            _playbackRate = _settings.PlaybackRate;
            _isShuffled = _settings.IsShuffled;
            _repeatMode = _settings.RepeatMode;
        }

        public void InitSMTC()
        {
            try
            {
                _smtc = _mediaPlayer.SystemMediaTransportControls;
                _smtc.IsEnabled = true;
                _smtc.IsPlayEnabled = true;
                _smtc.IsPauseEnabled = true;
                _smtc.IsNextEnabled = true;
                _smtc.IsPreviousEnabled = true;
                _smtc.ButtonPressed += SystemMediaControls_ButtonPressed;
                UpdateSMTCPlaylistState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SMTC init error: " + ex.Message);
            }
        }

        private void UpdateSMTCPlaylistState()
        {
            try
            {
                if (_smtc == null) return;
                bool hasMultiple = _filteredSongs.Count >= 2;
                _smtc.IsNextEnabled = hasMultiple;
                _smtc.IsPreviousEnabled = hasMultiple;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateSMTCPlaylistState error: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                if (_appWindow != null)
                {
                    var presenter = _appWindow.Presenter as OverlappedPresenter;
                    bool isMax = presenter?.State == OverlappedPresenterState.Maximized;
                    if (!isMax)
                    {
                        var pos = _appWindow.Position;
                        var sz = _appWindow.Size;
                        _settings.WindowX = pos.X;
                        _settings.WindowY = pos.Y;
                        _settings.WindowWidth = sz.Width;
                        _settings.WindowHeight = sz.Height;
                    }
                }

                _settings.Volume = _currentVolume;
                _settings.IsMuted = _isMuted;
                _settings.PlaybackRate = _playbackRate;
                _settings.IsShuffled = _isShuffled;
                _settings.RepeatMode = _repeatMode;
                _settings.LastSongPath = _currentSong?.FilePath;

                var session = _mediaPlayer.PlaybackSession;
                if (session.NaturalDuration.TotalSeconds > 0)
                    _settings.LastSeekPercent = session.Position.TotalSeconds / session.NaturalDuration.TotalSeconds * 100.0;
                else
                    _settings.LastSeekPercent = _simProgress;

                SettingsManager.Save(_settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveSettings error: {ex.Message}");
            }
        }

        private async void TryResumeLastSong()
        {
            try
            {
                if (string.IsNullOrEmpty(_settings.LastSongPath)) return;
                var song = _allSongs.FirstOrDefault(
                    s => string.Equals(s.FilePath, _settings.LastSongPath, StringComparison.OrdinalIgnoreCase));
                if (song == null) return;
                PlaylistView.SelectedItem = song;
                if (_settings.LastSeekPercent > 0)
                    SeekToPercent(_settings.LastSeekPercent);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TryResumeLastSong error: {ex.Message}");
            }
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            try
            {
                if (_smtc != null)
                    _smtc.ButtonPressed -= SystemMediaControls_ButtonPressed;
                SaveSettings();
                _mediaPlayer.Dispose();
                _progressTimer.Stop();
                _rippleTimer.Stop();
                _glowTimer.Stop();
                _autoSaveTimer.Stop();
                _playingAnimTimer?.Stop();
                _playBtnAnimTimer?.Stop();
                _albumArtStream?.Dispose();
                _fallbackStream?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Window_Closed error: {ex.Message}");
            }
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            try
            {
                if (!args.DidPresenterChange) return;

                var presenter = sender.Presenter as OverlappedPresenter;
                if (presenter == null) return;

                var currentState = presenter.State;

                if (_lastPresenterState == OverlappedPresenterState.Maximized &&
                    currentState != OverlappedPresenterState.Maximized)
                {
                    sender.MoveAndResize(new Windows.Graphics.RectInt32(
                        _normalWindowPosition.X, _normalWindowPosition.Y, 700, 350));
                }
                else if (_lastPresenterState != OverlappedPresenterState.Maximized &&
                         currentState == OverlappedPresenterState.Maximized)
                {
                    _normalWindowPosition = sender.Position;
                }

                _lastPresenterState = currentState;

                DispatcherQueue.TryEnqueue(() =>
                {
                    bool isMax = currentState == OverlappedPresenterState.Maximized;
                    BtnOpenSidebar.Visibility = isMax ? Visibility.Visible : Visibility.Collapsed;
                    if (_currentSong != null)
                        LblSongTitle.Text = GetDisplayTitle(_currentSong.Title);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AppWindow_Changed error: {ex.Message}");
            }
        }

        private void UpdateTitleBarText()
        {
            try
            {
                if (!_isInitialized || TitleBarText == null) return;
                bool isCompact = _lastWindowWidth < BreakMedium;
                TitleBarText.Text = (isCompact && _currentSong != null)
                    ? (_currentSong.Title.Length > 30 ? $"{_currentSong.Title[..30]}..." : _currentSong.Title)
                    : "Music Player";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateTitleBarText error: {ex.Message}");
            }
        }

        private string GetDisplayTitle(string title)
        {
            try
            {
                var presenter = _appWindow?.Presenter as OverlappedPresenter;
                bool isMax = presenter?.State == OverlappedPresenterState.Maximized;
                if (!isMax && title.Length > 30)
                    return title[..30] + "\u2026";
                return title;
            }
            catch { return title; }
        }

        private void ApplyAlbumBackground(BitmapImage? bmp)
        {
            try
            {
                _currentAlbumBitmap = bmp;
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (_currentAlbumBitmap != null)
                            BackgroundImage.Source = _currentAlbumBitmap;
                        else
                            BackgroundImage.Source = new BitmapImage { UriSource = new Uri("ms-appx:///Assets/blur_bg.jpg") };
                        BackgroundImage.Opacity = 1.0;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ApplyAlbumBackground UI error: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApplyAlbumBackground error: {ex.Message}");
            }
        }

        private void SetupMediaPlayer()
        {
            try
            {
                _mediaPlayer.Volume = _currentVolume;
                _mediaPlayer.PlaybackRate = _playbackRate;
                _mediaPlayer.AutoPlay = false;
                _mediaPlayer.CommandManager.IsEnabled = false;

                _mediaPlayer.MediaEnded += (s, e) =>
                    DispatcherQueue.TryEnqueue(() => HandleSongEnd());

                _mediaPlayer.MediaFailed += (s, e) =>
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        LblSongTitle.Text = "Playback Error";
                        LblArtistName.Text = e.ErrorMessage;
                        StopPlayback();
                        ShowTemporaryStatus($"Playback error: {e.ErrorMessage}");
                    });

                _mediaPlayer.PlaybackSession.PlaybackStateChanged += (s, e) =>
                    DispatcherQueue.TryEnqueue(() => SyncPlayStateFromMediaPlayer());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SetupMediaPlayer error: {ex.Message}");
                ShowTemporaryStatus("Media player initialization failed.");
            }
        }

        private void SyncPlayStateFromMediaPlayer()
        {
            try
            {
                if (_mediaPlayer.PlaybackSession != null)
                {
                    bool isActuallyPlaying = _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
                    if (_isPlaying != isActuallyPlaying)
                    {
                        _isPlaying = isActuallyPlaying;
                        UpdatePlayPauseButton();
                        if (_isPlaying)
                            StartPulse();
                        else
                            StopPulse();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SyncPlayStateFromMediaPlayer error: {ex.Message}");
            }
        }

        private void UpdatePlayPauseButton()
        {
            try
            {
                PlayPauseIcon.Glyph = _isPlaying ? "\uE769" : "\uE768";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdatePlayPauseButton error: {ex.Message}");
            }
        }

        private void SystemMediaControls_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    switch (args.Button)
                    {
                        case SystemMediaTransportControlsButton.Play:
                            if (_isPlaying) break;
                            _isPlaying = true;
                            _progressTimer.Start();
                            StartPulse();
                            if (!string.IsNullOrEmpty(_currentSong?.FilePath)) _mediaPlayer.Play();
                            UpdatePlayPauseButton();
                            UpdateSMTCState(true);
                            break;

                        case SystemMediaTransportControlsButton.Pause:
                            if (!_isPlaying) break;
                            _isPlaying = false;
                            _progressTimer.Stop();
                            StopPulse();
                            _mediaPlayer.Pause();
                            UpdatePlayPauseButton();
                            UpdateSMTCState(false);
                            break;

                        case SystemMediaTransportControlsButton.Next:
                            PlayNext();
                            break;

                        case SystemMediaTransportControlsButton.Previous:
                            PlayPrevious();
                            break;
                    }
                    SaveSettings();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"SystemMediaControls_ButtonPressed error: {ex.Message}");
                }
            });
        }

        private async void UpdateSystemTrayMetadata()
        {
            try
            {
                if (_currentSong == null || _smtc == null) return;

                var updater = _smtc.DisplayUpdater;
                updater.ClearAll();
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = _currentSong.Title;
                updater.MusicProperties.Artist = LblArtistName.Text;

                if (!string.IsNullOrEmpty(_smtcThumbnailPath) && System.IO.File.Exists(_smtcThumbnailPath))
                {
                    var sf = await StorageFile.GetFileFromPathAsync(_smtcThumbnailPath);
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(sf);
                }
                else if (_currentThumbnailStreamRef != null)
                {
                    updater.Thumbnail = _currentThumbnailStreamRef;
                }

                updater.Update();
                UpdateSMTCPlaylistState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating system tray metadata: {ex.Message}");
            }
        }

        private void Window_SizeChanged(object sender, WindowSizeChangedEventArgs args)
        {
            try
            {
                _lastWindowWidth = args.Size.Width;
                ApplyResponsiveLayout(args.Size.Width, args.Size.Height);
                UpdateTitleBarText();
                if (_currentSong != null)
                    LblSongTitle.Text = GetDisplayTitle(_currentSong.Title);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Window_SizeChanged error: {ex.Message}");
            }
        }

        private void ApplyResponsiveLayout(double w, double h)
        {
            try
            {
                if (!_isInitialized) return;

                double artDiam, songInfoMarginTop, titleFontSize, artistFontSize;
                double playBtnSize, navBtnSize, sideBtnSize;
                double playIconSize, navIconSize, sideIconSize;
                double controlsSpacing;
                double volBarWidth, speedSliderWidth;
                bool showSpeedLabel;

                if (w < BreakCompact)
                {
                    artDiam = Math.Min(130, h * 0.22);
                    songInfoMarginTop = artDiam + 40;
                    titleFontSize = 14; artistFontSize = 10;
                    playBtnSize = 46; navBtnSize = 34; sideBtnSize = 28;
                    playIconSize = 19; navIconSize = 14; sideIconSize = 12;
                    controlsSpacing = 7; volBarWidth = 70; speedSliderWidth = 55;
                    showSpeedLabel = false;
                }
                else if (w < BreakMedium)
                {
                    artDiam = Math.Min(190, h * 0.26);
                    songInfoMarginTop = artDiam + 50;
                    titleFontSize = 19; artistFontSize = 12;
                    playBtnSize = 54; navBtnSize = 40; sideBtnSize = 32;
                    playIconSize = 21; navIconSize = 16; sideIconSize = 13;
                    controlsSpacing = 9; volBarWidth = 90; speedSliderWidth = 65;
                    showSpeedLabel = true;
                    BtnOpenSidebar.Visibility = Visibility.Collapsed;
                }
                else if (w < 720)
                {
                    artDiam = Math.Min(220, h * 0.30);
                    songInfoMarginTop = artDiam + 65;
                    titleFontSize = 22; artistFontSize = 13;
                    playBtnSize = 60; navBtnSize = 44; sideBtnSize = 38;
                    playIconSize = 24; navIconSize = 18; sideIconSize = 16;
                    controlsSpacing = 12; volBarWidth = 110; speedSliderWidth = 72;
                    showSpeedLabel = true;
                }
                else
                {
                    artDiam = Math.Min(260, h * 0.32);
                    songInfoMarginTop = artDiam + 75;
                    titleFontSize = 26; artistFontSize = 15;
                    playBtnSize = 70; navBtnSize = 52; sideBtnSize = 44;
                    playIconSize = 28; navIconSize = 22; sideIconSize = 19;
                    controlsSpacing = 14; volBarWidth = 140; speedSliderWidth = 90;
                    showSpeedLabel = true;
                }

                double halfDiam = artDiam / 2.0;
                _artRadius = halfDiam;
                AlbumOuterBorder.Width = artDiam; AlbumOuterBorder.Height = artDiam;
                AlbumOuterBorder.CornerRadius = new CornerRadius(halfDiam);
                AlbumInnerBorder.CornerRadius = new CornerRadius(Math.Max(0, halfDiam - 3));

                SongInfoPanel.Margin = new Thickness(0, songInfoMarginTop, 0, 0);
                LblSongTitle.FontSize = titleFontSize;
                LblSongTitle.MaxWidth = Math.Max(180, w - 80);
                LblArtistName.FontSize = artistFontSize;

                ControlsStack.Spacing = controlsSpacing;
                TransportPanel.Spacing = Math.Max(4, controlsSpacing - 2);

                SetCircleButton(PlayBtnBorder, BtnPlayPause, PlayPauseIcon, playBtnSize, playIconSize);
                SetCircleButtonBorder(PrevBorder, BtnPrev, PrevIcon, navBtnSize, navIconSize);
                SetCircleButtonBorder(NextBorder, BtnNext, NextIcon, navBtnSize, navIconSize);
                SetSideButton(ShuffleBorder, BtnShuffle, ShuffleIcon, sideBtnSize, sideIconSize);
                SetSideButton(RepeatBorder, BtnRepeat, RepeatIcon, sideBtnSize, sideIconSize);

                VolumeBarGrid.Width = volBarWidth;
                UpdateVolumeUI();
                TrkSpeed.Width = speedSliderWidth;
                SpdLabel.Visibility = showSpeedLabel ? Visibility.Visible : Visibility.Collapsed;
                SidebarPanel.Width = Math.Min(300, w * 0.85);
                TitleBarText.Visibility = w < 300 ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApplyResponsiveLayout error: {ex.Message}");
            }
        }

        private static void SetCircleButton(Border b, Button btn, FontIcon icon, double size, double iconSize)
        { b.Width = size; b.Height = size; b.CornerRadius = new CornerRadius(size / 2.0); btn.Width = size; btn.Height = size; btn.CornerRadius = new CornerRadius(size / 2.0); icon.FontSize = iconSize; }
        private static void SetCircleButtonBorder(Border b, Button btn, FontIcon icon, double size, double iconSize)
        { b.Width = size; b.Height = size; b.CornerRadius = new CornerRadius(size / 2.0); btn.Width = size; btn.Height = size; btn.CornerRadius = new CornerRadius(size / 2.0); icon.FontSize = iconSize; }
        private static void SetSideButton(Border b, Button btn, FontIcon icon, double size, double iconSize)
        { b.Width = size; b.Height = size; b.CornerRadius = new CornerRadius(size / 2.0); btn.Width = size; btn.Height = size; btn.CornerRadius = new CornerRadius(size / 2.0); icon.FontSize = iconSize; }

        private async void BtnScanMusic_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;

                LblScanStatus.Visibility = Visibility.Visible;
                LblScanStatus.Text = "Scanning…";
                OpenSidebar();

                var queryOptions = new QueryOptions(CommonFileQuery.OrderByName,
                    new List<string> { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma", ".opus", ".aiff" })
                { FolderDepth = FolderDepth.Deep };
                var files = await folder.CreateFileQueryWithOptions(queryOptions).GetFilesAsync();

                if (files.Count == 0) { LblScanStatus.Text = "No audio files found."; return; }

                LblScanStatus.Text = $"Loading {files.Count} files…";
                _allSongs.Clear();
                int loaded = 0;

                foreach (var file in files)
                {
                    try
                    {
                        string title = "";
                        string artist = "";
                        string duration = "--:--";
                        int durationSeconds = 0;

                        using (var tagFile = TagLib.File.Create(file.Path))
                        {
                            if (!string.IsNullOrEmpty(tagFile.Tag.Title))
                                title = tagFile.Tag.Title;
                            else
                                title = System.IO.Path.GetFileNameWithoutExtension(file.Name);

                            if (!string.IsNullOrEmpty(tagFile.Tag.FirstAlbumArtist))
                                artist = tagFile.Tag.FirstAlbumArtist;
                            else if (!string.IsNullOrEmpty(tagFile.Tag.FirstArtist))
                                artist = tagFile.Tag.FirstArtist;
                            else if (!string.IsNullOrEmpty(tagFile.Tag.JoinedArtists))
                                artist = tagFile.Tag.JoinedArtists;

                            if (string.IsNullOrEmpty(artist))
                            {
                                var props = await file.Properties.GetMusicPropertiesAsync();
                                artist = string.IsNullOrWhiteSpace(props.Artist) ? "Unknown Artist" : props.Artist;
                            }

                            if (tagFile.Properties.Duration.TotalSeconds > 0)
                            {
                                var dur = tagFile.Properties.Duration;
                                duration = $"{(int)dur.TotalMinutes}:{dur.Seconds:D2}";
                                durationSeconds = (int)dur.TotalSeconds;
                            }
                            else
                            {
                                var props = await file.Properties.GetMusicPropertiesAsync();
                                var dur = props.Duration;
                                duration = $"{(int)dur.TotalMinutes}:{dur.Seconds:D2}";
                                durationSeconds = (int)dur.TotalSeconds;
                            }
                        }

                        _allSongs.Add(new Song
                        {
                            Title = title,
                            Artist = artist,
                            FilePath = file.Path,
                            Duration = duration,
                            DurationInSeconds = durationSeconds
                        });
                        loaded++;
                        if (loaded % 10 == 0) LblScanStatus.Text = $"Loaded {loaded}/{files.Count}…";
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error scanning file {file.Path}: {ex.Message}");
                    }
                }

                RefreshFilteredList(TxtSearch.Text);
                LblScanStatus.Text = $"{_allSongs.Count} songs loaded";
                if (_isShuffled) RebuildShuffleOrder();
                if (_allSongs.Count > 0 && _currentSong == null)
                    PlaylistView.SelectedItem = _filteredSongs.FirstOrDefault();

                UpdateSMTCPlaylistState();
                SaveSettings();
            }
            catch (Exception ex)
            {
                LblScanStatus.Visibility = Visibility.Visible;
                LblScanStatus.Text = $"Error: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"BtnScanMusic_Click error: {ex.Message}");
            }
        }

        private void InitializeSampleData()
        {
            try
            {
                _allSongs.Clear();
                string[] exts = { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma" };
                string[] folders =
                {
                    System.IO.Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
                    System.IO.Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
                    System.IO.Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                };
                foreach (string folder in folders)
                {
                    if (!Directory.Exists(folder)) continue;
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                        {
                            if (!exts.Contains(System.IO.Path.GetExtension(file).ToLower())) continue;

                            string artist = "Unknown Artist";
                            string title = System.IO.Path.GetFileNameWithoutExtension(file);
                            string duration = "--:--";
                            int durationSeconds = 0;

                            try
                            {
                                using (var tagFile = TagLib.File.Create(file))
                                {
                                    if (!string.IsNullOrEmpty(tagFile.Tag.Title))
                                        title = tagFile.Tag.Title;

                                    if (!string.IsNullOrEmpty(tagFile.Tag.FirstAlbumArtist))
                                        artist = tagFile.Tag.FirstAlbumArtist;
                                    else if (!string.IsNullOrEmpty(tagFile.Tag.FirstArtist))
                                        artist = tagFile.Tag.FirstArtist;
                                    else if (!string.IsNullOrEmpty(tagFile.Tag.JoinedArtists))
                                        artist = tagFile.Tag.JoinedArtists;

                                    if (tagFile.Properties.Duration.TotalSeconds > 0)
                                    {
                                        var dur = tagFile.Properties.Duration;
                                        duration = $"{(int)dur.TotalMinutes}:{dur.Seconds:D2}";
                                        durationSeconds = (int)dur.TotalSeconds;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"TagLib error for {file}: {ex.Message}");
                            }

                            _allSongs.Add(new Song
                            {
                                Title = title,
                                Artist = artist,
                                Duration = duration,
                                DurationInSeconds = durationSeconds,
                                FilePath = file
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Folder enumeration error {folder}: {ex.Message}");
                    }
                }
                _filteredSongs = new ObservableCollection<Song>(_allSongs);
                PlaylistView.ItemsSource = _filteredSongs;
                UpdateSMTCPlaylistState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InitializeSampleData error: {ex.Message}");
            }
        }

        private void RefreshFilteredList(string query)
        {
            try
            {
                _filteredSongs.Clear();
                foreach (var s in _allSongs.Where(s =>
                    string.IsNullOrEmpty(query) ||
                    s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    s.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    _filteredSongs.Add(s);
                PlaylistView.ItemsSource = _filteredSongs;
                if (_isShuffled) RebuildShuffleOrder();
                UpdateSMTCPlaylistState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshFilteredList error: {ex.Message}");
            }
        }

        private void RebuildShuffleOrder()
        {
            try
            {
                _shuffledOrder = _filteredSongs.OrderBy(_ => _random.Next()).ToList();
                if (_currentSong != null)
                {
                    int pos = _shuffledOrder.IndexOf(_currentSong);
                    if (pos < 0) { _shuffledOrder.Insert(0, _currentSong); pos = 0; }
                    _shuffledOrder.RemoveAt(pos);
                    _shuffledOrder.Insert(0, _currentSong);
                    _shuffleIndex = 0;
                }
                else _shuffleIndex = -1;
                UpdateSMTCPlaylistState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RebuildShuffleOrder error: {ex.Message}");
            }
        }

        private Song? GetNextShuffled()
        {
            try
            {
                if (_shuffledOrder.Count == 0) return null;
                _shuffleIndex++;
                if (_shuffleIndex >= _shuffledOrder.Count)
                {
                    RebuildShuffleOrder();
                    _shuffleIndex = 0;
                }
                return _shuffledOrder[_shuffleIndex];
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetNextShuffled error: {ex.Message}");
                return null;
            }
        }

        private Song? GetPrevShuffled()
        {
            try
            {
                if (_shuffledOrder.Count == 0) return null;
                _shuffleIndex = Math.Max(0, _shuffleIndex - 1);
                return _shuffledOrder[_shuffleIndex];
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetPrevShuffled error: {ex.Message}");
                return null;
            }
        }

        private void SetupTimers()
        {
            try
            {
                _progressTimer.Interval = TimeSpan.FromMilliseconds(250);
                _progressTimer.Tick += ProgressTimer_Tick;

                _rippleTimer.Interval = TimeSpan.FromMilliseconds(50);
                _rippleTimer.Tick += RippleTimer_Tick;

                _glowTimer.Interval = TimeSpan.FromMilliseconds(33);
                _glowTimer.Tick += GlowTimer_Tick;

                _autoSaveTimer.Interval = TimeSpan.FromSeconds(10);
                _autoSaveTimer.Tick += (_, _) => SaveSettings();
                _autoSaveTimer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SetupTimers error: {ex.Message}");
            }
        }

        private void ProgressTimer_Tick(object? sender, object e)
        {
            try
            {
                if (_isSeeking) return;
                var session = _mediaPlayer.PlaybackSession;
                bool hasRealMedia = _currentSong != null && !string.IsNullOrEmpty(_currentSong.FilePath);

                if (hasRealMedia)
                {
                    if (session.NaturalDuration.TotalSeconds > 0)
                    {
                        double pct = session.Position.TotalSeconds / session.NaturalDuration.TotalSeconds * 100.0;
                        UpdateProgressUI(pct, session.Position, session.NaturalDuration);
                    }
                }
                else
                {
                    if (!_isPlaying || _currentSong == null) return;
                    double simPct = GetSimulatedProgress() + 0.5 * _playbackRate;

                    if (simPct >= 100)
                    {
                        simPct = 0;
                        HandleSongEnd();
                    }
                    else
                    {
                        SetSimulatedProgress(simPct);
                        double total = _currentSong.DurationInSeconds;
                        UpdateProgressUI(simPct, TimeSpan.FromSeconds(total * simPct / 100.0), TimeSpan.FromSeconds(total));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ProgressTimer_Tick error: {ex.Message}");
            }
        }

        private double _simProgress;
        private double GetSimulatedProgress() => _simProgress;
        private void SetSimulatedProgress(double v) => _simProgress = v;

        private void UpdateProgressUI(double pct, TimeSpan elapsed, TimeSpan total)
        {
            try
            {
                double barW = ProgressHitTarget.ActualWidth;
                if (barW > 0)
                {
                    double fill = barW * pct / 100.0;
                    ProgressFill.Width = Math.Max(0, fill);
                    ProgressThumb.Margin = new Thickness(fill - 7, 0, 0, 0);
                    ProgressThumbRing.Margin = new Thickness(fill - 9, 0, 0, 0);
                }
                LblTimeElapsed.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
                LblTimeTotal.Text = $"{(int)total.TotalMinutes}:{total.Seconds:D2}";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateProgressUI error: {ex.Message}");
            }
        }

        private void SetupRippleElements()
        {
            try
            {
                _glowEllipse = new Ellipse { Width = 230, Height = 230, IsHitTestVisible = false, Opacity = 0, Fill = new SolidColorBrush(Color.FromArgb(40, 0, 245, 255)) };
                Canvas.SetLeft(_glowEllipse, 200 - 115); Canvas.SetTop(_glowEllipse, 200 - 115);
                AlbumRippleCanvas.Children.Add(_glowEllipse);

                _ring1 = CreateRippleEllipse(); _ring2 = CreateRippleEllipse(); _ring3 = CreateRippleEllipse();
                AlbumRippleCanvas.Children.Add(_ring1); AlbumRippleCanvas.Children.Add(_ring2); AlbumRippleCanvas.Children.Add(_ring3);

                _pulse1R = _artRadius; _pulse1A = 200;
                _pulse2R = _artRadius + 28; _pulse2A = 140;
                _pulse3R = _artRadius + 56; _pulse3A = 80;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SetupRippleElements error: {ex.Message}");
            }
        }

        private static Ellipse CreateRippleEllipse() =>
            new Ellipse { IsHitTestVisible = false, Opacity = 0, StrokeThickness = 2.5, Fill = null };

        private void RippleTimer_Tick(object? sender, object e)
        {
            try
            {
                if (!_isPlaying) return;
                const double speed = 2.2;
                AdvancePulse(ref _pulse1R, ref _pulse1A, speed, _artRadius, MaxRippleR, 200.0, 0.00);
                AdvancePulse(ref _pulse2R, ref _pulse2A, speed * 0.70, _artRadius, MaxRippleR, 150.0, 0.33);
                AdvancePulse(ref _pulse3R, ref _pulse3A, speed * 0.50, _artRadius, MaxRippleR, 100.0, 0.66);
                ApplyRipple(_ring1!, _pulse1R, _pulse1A, Color.FromArgb(255, 0, 245, 255), Color.FromArgb(255, 20, 184, 166));
                ApplyRipple(_ring2!, _pulse2R, _pulse2A, Color.FromArgb(255, 168, 85, 247), Color.FromArgb(255, 236, 72, 153));
                ApplyRipple(_ring3!, _pulse3R, _pulse3A, Color.FromArgb(255, 0, 245, 255), Color.FromArgb(255, 168, 85, 247));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RippleTimer_Tick error: {ex.Message}");
            }
        }

        private void GlowTimer_Tick(object? sender, object e)
        {
            try
            {
                _glowPhase += 0.06;
                if (_glowPhase > Math.PI * 2) _glowPhase -= Math.PI * 2;
                if (_glowEllipse == null) return;
                double amp = 0.5 + 0.5 * Math.Sin(_glowPhase);
                byte alpha = _isPlaying ? (byte)(25 + 30 * amp) : (byte)0;
                _glowEllipse.Fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 245, 255));
                _glowEllipse.Opacity = 1.0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GlowTimer_Tick error: {ex.Message}");
            }
        }

        private static void AdvancePulse(ref double r, ref double a, double speed, double minR, double maxR, double resetA, double phase)
        {
            double range = maxR - minR; r += speed; a -= resetA / (range / speed);
            if (r >= maxR || a <= 0) { r = minR + range * phase; a = resetA; }
        }

        private static void ApplyRipple(Ellipse el, double r, double a, Color c1, Color c2)
        {
            double d = r * 2; el.Width = d; el.Height = d;
            Canvas.SetLeft(el, 200 - r); Canvas.SetTop(el, 200 - r);
            byte ab = (byte)Math.Max(0, Math.Min(255, a));
            el.Stroke = new SolidColorBrush(Color.FromArgb(ab, (byte)((c1.R + c2.R) / 2), (byte)((c1.G + c2.G) / 2), (byte)((c1.B + c2.B) / 2)));
            el.Opacity = ab / 255.0;
        }

        private void StartPulse() { if (!_rippleTimer.IsEnabled) _rippleTimer.Start(); if (!_glowTimer.IsEnabled) _glowTimer.Start(); StartPlayButtonAnimation(); }
        private void StopPulse()
        {
            _rippleTimer.Stop(); _glowTimer.Stop();
            if (_ring1 != null) _ring1.Opacity = 0; if (_ring2 != null) _ring2.Opacity = 0; if (_ring3 != null) _ring3.Opacity = 0;
            if (_glowEllipse != null) _glowEllipse.Opacity = 0;
            StopPlayButtonAnimation();
        }

        private void HandleSongEnd()
        {
            try
            {
                switch (_repeatMode)
                {
                    case RepeatMode.One:
                        SeekToPercent(0);
                        if (!string.IsNullOrEmpty(_currentSong?.FilePath))
                            _mediaPlayer.Play();
                        else
                            StopPlayback();
                        break;

                    case RepeatMode.All:
                        if (_isShuffled)
                            PlayNextShuffled();
                        else
                            PlayNextSequential(wrap: true);
                        break;

                    default:
                        if (_isShuffled)
                            PlayNextShuffled();
                        else
                            PlayNextSequential(wrap: false);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HandleSongEnd error: {ex.Message}");
                StopPlayback();
            }
        }

        private void PlayNextShuffled() { var n = GetNextShuffled(); if (n == null) { StopPlayback(); return; } PlaylistView.SelectedItem = n; PlaySong(n); }
        private void PlayPrevShuffled() { var p = GetPrevShuffled(); if (p == null) return; PlaylistView.SelectedItem = p; PlaySong(p); }

        private void PlayNextSequential(bool wrap)
        {
            try
            {
                if (_currentSong == null && _filteredSongs.Count > 0) { PlaySong(_filteredSongs[0]); return; }
                int idx = _filteredSongs.IndexOf(_currentSong!);
                if (idx < _filteredSongs.Count - 1) { var n = _filteredSongs[idx + 1]; PlaylistView.SelectedItem = n; PlaySong(n); }
                else if (wrap && _filteredSongs.Count > 0) { PlaylistView.SelectedItem = _filteredSongs[0]; PlaySong(_filteredSongs[0]); }
                else StopPlayback();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PlayNextSequential error: {ex.Message}");
            }
        }

        private void PlayPreviousSequential()
        {
            try
            {
                if (_currentSong == null && _filteredSongs.Count > 0) { PlaySong(_filteredSongs[0]); return; }
                int idx = _filteredSongs.IndexOf(_currentSong!);
                if (idx > 0) { var p = _filteredSongs[idx - 1]; PlaylistView.SelectedItem = p; PlaySong(p); }
                else { var last = _filteredSongs[^1]; PlaylistView.SelectedItem = last; PlaySong(last); }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PlayPreviousSequential error: {ex.Message}");
            }
        }

        private void PlayNext() { if (_isShuffled) PlayNextShuffled(); else PlayNextSequential(wrap: _repeatMode == RepeatMode.All); }
        private void PlayPrevious() { if (_isShuffled) PlayPrevShuffled(); else PlayPreviousSequential(); }

        private void StopPlayback()
        {
            try
            {
                _isPlaying = false;
                _progressTimer.Stop();
                StopPulse();
                StopPlayingAnimation();
                _mediaPlayer.Pause();
                UpdatePlayPauseButton();
                UpdateSMTCState(false);
                SaveSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"StopPlayback error: {ex.Message}");
            }
        }

        private static readonly Random _randomThumb = new Random();

        private async void PlaySong(Song song)
        {
            try
            {
                _currentSong = song;

                string artistName = song.Artist;
                string titleName = song.Title;

                if (!string.IsNullOrEmpty(song.FilePath) && System.IO.File.Exists(song.FilePath))
                {
                    try
                    {
                        using (var file = TagLib.File.Create(song.FilePath))
                        {
                            if (!string.IsNullOrEmpty(file.Tag.FirstAlbumArtist))
                                artistName = file.Tag.FirstAlbumArtist;
                            else if (!string.IsNullOrEmpty(file.Tag.FirstArtist))
                                artistName = file.Tag.FirstArtist;
                            else if (!string.IsNullOrEmpty(file.Tag.JoinedArtists))
                                artistName = file.Tag.JoinedArtists;

                            if (!string.IsNullOrEmpty(file.Tag.Title))
                                titleName = file.Tag.Title;

                            song.Artist = artistName;
                            song.Title = titleName;

                            if (file.Properties.Duration.TotalSeconds > 0)
                            {
                                var duration = file.Properties.Duration;
                                song.Duration = $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}";
                                song.DurationInSeconds = (int)duration.TotalSeconds;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"TagLib error: {ex.Message}");
                    }
                }

                if (string.IsNullOrWhiteSpace(artistName) || artistName == "Unknown Artist")
                {
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(song.FilePath);
                    var parts = fileName.Split(new[] { " - ", " – " }, StringSplitOptions.None);
                    artistName = parts.Length >= 2 ? parts[0] : "Unknown Artist";
                }

                LblSongTitle.Text = GetDisplayTitle(titleName);
                LblArtistName.Text = artistName;
                LblTimeTotal.Text = song.Duration;
                _simProgress = 0;

                UpdateTitleBarText();

                bool hasRealFile = !string.IsNullOrEmpty(song.FilePath) && System.IO.File.Exists(song.FilePath);

                if (hasRealFile)
                {
                    try
                    {
                        var storageFile = await StorageFile.GetFileFromPathAsync(song.FilePath);

                        _mediaPlayer.Source = MediaSource.CreateFromStorageFile(storageFile);
                        _mediaPlayer.Volume = _isMuted ? 0 : _currentVolume;
                        _mediaPlayer.PlaybackRate = _playbackRate;

                        bool hasEmbeddedArt = false;

                        try
                        {
                            using (var tagFile = TagLib.File.Create(song.FilePath))
                            {
                                var pictures = tagFile.Tag.Pictures;

                                if (pictures != null && pictures.Length > 0)
                                {
                                    hasEmbeddedArt = true;

                                    var picture = pictures[0];
                                    _albumArtStream?.Dispose();
                                    _albumArtStream = new MemoryStream(picture.Data.Data);

                                    var bmp = new BitmapImage();
                                    await bmp.SetSourceAsync(_albumArtStream.AsRandomAccessStream());
                                    AlbumArt.Source = bmp;
                                    ApplyAlbumBackground(bmp);

                                    try
                                    {
                                        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                                        var thumbFolder = System.IO.Path.Combine(appData, "WinUIMusicPlayer");
                                        System.IO.Directory.CreateDirectory(thumbFolder);

                                        foreach (var oldFile in System.IO.Directory.GetFiles(thumbFolder, "smtcthumbnail.*"))
                                            System.IO.File.Delete(oldFile);

                                        string ext = ".jpg";
                                        if (picture.MimeType?.Contains("png") == true) ext = ".png";
                                        else if (picture.MimeType?.Contains("bmp") == true) ext = ".bmp";

                                        _smtcThumbnailPath = System.IO.Path.Combine(thumbFolder, $"smtcthumbnail{ext}");
                                        await System.IO.File.WriteAllBytesAsync(_smtcThumbnailPath, picture.Data.Data);

                                        _currentThumbnailStreamRef = RandomAccessStreamReference.CreateFromUri(new Uri(_smtcThumbnailPath));
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"SMTC thumbnail save error: {ex.Message}");
                                        var smtcStream = new MemoryStream(picture.Data.Data);
                                        _currentThumbnailStreamRef = RandomAccessStreamReference.CreateFromStream(smtcStream.AsRandomAccessStream());
                                    }

                                    UpdateSystemTrayMetadata();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Album art error: {ex.Message}");
                        }

                        if (!hasEmbeddedArt)
                            await LoadRandomFallbackImage();

                        _mediaPlayer.Play();
                    }
                    catch (Exception ex)
                    {
                        LblSongTitle.Text = "Error loading";
                        LblArtistName.Text = ex.Message;
                        await LoadRandomFallbackImage();
                        ShowTemporaryStatus($"Failed to load song: {ex.Message}");
                    }
                }
                else
                {
                    await LoadRandomFallbackImage();
                }

                if (!hasRealFile || (hasRealFile && _currentThumbnailStreamRef == null))
                    UpdateSystemTrayMetadata();

                _isPlaying = true;
                UpdatePlayPauseButton();
                _progressTimer.Start();
                StartPulse();
                UpdateSMTCState(true);
                UpdateSMTCPlaylistState();

                int playingIdx = _filteredSongs.IndexOf(_currentSong);
                UpdatePlayingItemBorder(playingIdx, true);

                SaveSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PlaySong error: {ex.Message}");
                ShowTemporaryStatus("Playback failed: " + ex.Message);
            }
        }

        private void UpdateSMTCState(bool isPlaying)
        {
            try
            {
                if (_smtc == null) return;
                _smtc.PlaybackStatus = isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SMTC state update error: {ex.Message}");
            }
        }

        private async Task LoadRandomFallbackImage()
        {
            _smtcThumbnailPath = null;

            // Use a simple string builder for logging to avoid file locking issues
            var logMessages = new List<string>();

            void Log(string message)
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                var logMsg = $"{timestamp} - {message}";
                logMessages.Add(logMsg);
                System.Diagnostics.Debug.WriteLine(logMsg);
            }

            try
            {
                int imageIndex = _randomThumb.Next(1, 12);
                Log($"=== Attempting image index: {imageIndex} ===");

                BitmapImage? fallbackBmp = null;

                // Method 1: Try to get from assembly location
                try
                {
                    string assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    string assemblyDir = System.IO.Path.GetDirectoryName(assemblyLocation);
                    Log($"Assembly location: {assemblyDir}");

                    string imagePath = System.IO.Path.Combine(assemblyDir, "Assets", "ThumbImages", $"{imageIndex}.jpg");
                    Log($"Checking path: {imagePath}");

                    if (System.IO.File.Exists(imagePath))
                    {
                        Log($"File exists! Loading...");
                        using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
                        {
                            var randomAccessStream = stream.AsRandomAccessStream();
                            fallbackBmp = new BitmapImage();
                            await fallbackBmp.SetSourceAsync(randomAccessStream);
                            Log($"✓ Successfully loaded image from: {imagePath}");

                            // For SMTC
                            _currentThumbnailStreamRef = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(imagePath));
                            _smtcThumbnailPath = imagePath;
                        }
                    }
                    else
                    {
                        Log($"File NOT found at: {imagePath}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Method 1 failed: {ex.Message}");
                }

                // Method 2: Try from current directory
                if (fallbackBmp == null)
                {
                    try
                    {
                        string currentDir = Environment.CurrentDirectory;
                        Log($"Current directory: {currentDir}");

                        string imagePath = System.IO.Path.Combine(currentDir, "Assets", "ThumbImages", $"{imageIndex}.jpg");
                        Log($"Checking path: {imagePath}");

                        if (System.IO.File.Exists(imagePath))
                        {
                            Log($"File exists! Loading...");
                            using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
                            {
                                var randomAccessStream = stream.AsRandomAccessStream();
                                fallbackBmp = new BitmapImage();
                                await fallbackBmp.SetSourceAsync(randomAccessStream);
                                Log($"✓ Successfully loaded image from: {imagePath}");

                                _currentThumbnailStreamRef = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(imagePath));
                                _smtcThumbnailPath = imagePath;
                            }
                        }
                        else
                        {
                            Log($"File NOT found at: {imagePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Method 2 failed: {ex.Message}");
                    }
                }

                // Method 3: Try ms-appx URI
                if (fallbackBmp == null)
                {
                    try
                    {
                        Log("Method 3: Trying ms-appx URI...");
                        var imageUri = new Uri($"ms-appx:///Assets/ThumbImages/{imageIndex}.jpg");
                        Log($"URI: {imageUri}");

                        fallbackBmp = new BitmapImage(imageUri);

                        // Wait a bit for the image to load
                        await Task.Delay(100);

                        if (fallbackBmp.PixelWidth > 0)
                        {
                            Log($"✓ Successfully loaded via ms-appx, Size: {fallbackBmp.PixelWidth}x{fallbackBmp.PixelHeight}");

                            try
                            {
                                var storageFile = await StorageFile.GetFileFromApplicationUriAsync(imageUri);
                                _currentThumbnailStreamRef = RandomAccessStreamReference.CreateFromFile(storageFile);
                                Log("✓ SMTC thumbnail created from URI");
                            }
                            catch (Exception ex)
                            {
                                Log($"SMTC thumbnail creation failed: {ex.Message}");
                            }
                        }
                        else
                        {
                            Log("Image loaded but dimensions are zero, might be placeholder");
                            // Keep it, it might still work
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Method 3 failed: {ex.Message}");
                        fallbackBmp = null;
                    }
                }

                // Set the album art
                if (fallbackBmp != null)
                {
                    AlbumArt.Source = fallbackBmp;
                    ApplyAlbumBackground(fallbackBmp);
                    Log("✓ Album art and background set successfully");
                }
                else
                {
                    Log("✗ All methods failed to load fallback image");

                    // Ultimate fallback - just use the app logo or nothing
                    try
                    {
                        var defaultImage = new BitmapImage(new Uri("ms-appx:///Assets/StoreLogo.png"));
                        AlbumArt.Source = defaultImage;
                        ApplyAlbumBackground(defaultImage);
                        _currentThumbnailStreamRef = RandomAccessStreamReference.CreateFromUri(new Uri("ms-appx:///Assets/StoreLogo.png"));
                        Log("✓ Using StoreLogo as fallback");
                    }
                    catch
                    {
                        AlbumArt.Source = null;
                        ApplyAlbumBackground(null);
                        Log("No fallback available");
                    }
                }

                // Save log to file
                try
                {
                    string settingsFolder = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CrystalPlayer");

                    if (!Directory.Exists(settingsFolder))
                        Directory.CreateDirectory(settingsFolder);

                    string logPath = System.IO.Path.Combine(settingsFolder, "fallback_log.txt");
                    await File.AppendAllLinesAsync(logPath, logMessages);
                }
                catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CRITICAL ERROR in LoadRandomFallbackImage: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                AlbumArt.Source = null;
                ApplyAlbumBackground(null);
                _currentThumbnailStreamRef = null;
            }

            UpdateSystemTrayMetadata();
        }

        private async Task<BitmapImage> CreateColoredFallbackImage()
        {
            var writeableBmp = new WriteableBitmap(200, 200);
            var pixels = new byte[200 * 200 * 4];

            var colors = new[]
            {
        Color.FromArgb(255, 0, 245, 255),   // Cyan
        Color.FromArgb(255, 168, 85, 247),  // Purple
        Color.FromArgb(255, 236, 72, 153),  // Pink
    };

            var color = colors[_randomThumb.Next(colors.Length)];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = 255;
            }

            using (var stream = writeableBmp.PixelBuffer.AsStream())
            {
                await stream.WriteAsync(pixels, 0, pixels.Length);
            }

            var bitmapImage = new BitmapImage();
            using (var memStream = new MemoryStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memStream.AsRandomAccessStream());
                encoder.SetSoftwareBitmap(SoftwareBitmap.CreateCopyFromBuffer(
                    writeableBmp.PixelBuffer,
                    BitmapPixelFormat.Bgra8,
                    200, 200));
                await encoder.FlushAsync();

                memStream.Seek(0, SeekOrigin.Begin);
                await bitmapImage.SetSourceAsync(memStream.AsRandomAccessStream());
            }

            return bitmapImage;
        }

        private async Task<RandomAccessStreamReference> CreateColoredFallbackStream()
        {
            var writeableBmp = new WriteableBitmap(200, 200);
            var pixels = new byte[200 * 200 * 4];

            var colors = new[]
            {
        Color.FromArgb(255, 0, 245, 255),   // Cyan
        Color.FromArgb(255, 168, 85, 247),  // Purple
        Color.FromArgb(255, 236, 72, 153),  // Pink
    };

            var color = colors[_randomThumb.Next(colors.Length)];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = 255;
            }

            using (var stream = writeableBmp.PixelBuffer.AsStream())
            {
                await stream.WriteAsync(pixels, 0, pixels.Length);
            }

            var memStream = new MemoryStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memStream.AsRandomAccessStream());
            encoder.SetSoftwareBitmap(SoftwareBitmap.CreateCopyFromBuffer(
                writeableBmp.PixelBuffer,
                BitmapPixelFormat.Bgra8,
                200, 200));
            await encoder.FlushAsync();

            memStream.Seek(0, SeekOrigin.Begin);
            return RandomAccessStreamReference.CreateFromStream(memStream.AsRandomAccessStream());
        }

        private void SeekToPercent(double pct)
        {
            try
            {
                var session = _mediaPlayer.PlaybackSession;
                if (session.NaturalDuration.TotalSeconds > 0)
                    session.Position = TimeSpan.FromSeconds(session.NaturalDuration.TotalSeconds * pct / 100.0);
                else
                    _simProgress = pct;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SeekToPercent error: {ex.Message}");
            }
        }

        private void UpdateVolumeUI()
        {
            try
            {
                if (VolumeFill == null) return;
                double barW = VolumeBarGrid?.Width ?? 110;
                double w = barW * _currentVolume;
                VolumeFill.Width = Math.Max(0, w);
                VolumeThumb.Margin = new Thickness(w - 6, 0, 0, 0);
                LblVolPct.Text = $"{(int)(_currentVolume * 100)}%";
                VolumeIcon.Glyph = (_isMuted || _currentVolume == 0) ? "\uE74F" : "\uE767";
                _mediaPlayer.Volume = _isMuted ? 0 : _currentVolume;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateVolumeUI error: {ex.Message}");
            }
        }

        private void OpenSidebar()
        {
            try
            {
                SidebarScrim.Visibility = Visibility.Visible;
                var anim = new DoubleAnimation { From = -340, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(280)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                var sb = new Storyboard(); sb.Children.Add(anim);
                Storyboard.SetTarget(anim, SidebarTransform); Storyboard.SetTargetProperty(anim, "TranslateX");
                sb.Begin();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OpenSidebar error: {ex.Message}");
            }
        }

        private void CloseSidebar()
        {
            try
            {
                var anim = new DoubleAnimation { From = 0, To = -340, Duration = new Duration(TimeSpan.FromMilliseconds(240)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                var sb = new Storyboard(); sb.Children.Add(anim);
                Storyboard.SetTarget(anim, SidebarTransform); Storyboard.SetTargetProperty(anim, "TranslateX");
                sb.Completed += (_, _) => SidebarScrim.Visibility = Visibility.Collapsed;
                sb.Begin();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CloseSidebar error: {ex.Message}");
                SidebarScrim.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnOpenSidebar_Click(object sender, RoutedEventArgs e) => OpenSidebar();
        private void BtnCloseSidebar_Click(object sender, RoutedEventArgs e) => CloseSidebar();
        private void SidebarScrim_Tapped(object sender, TappedRoutedEventArgs e) => CloseSidebar();

        private void BtnShuffle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isShuffled = !_isShuffled;
                if (_isShuffled)
                    RebuildShuffleOrder();
                else
                {
                    _shuffledOrder.Clear();
                    _shuffleIndex = -1;
                }
                ApplyShuffleUI();
                UpdateSMTCPlaylistState();
                SaveSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BtnShuffle_Click error: {ex.Message}");
            }
        }

        private void ApplyShuffleUI()
        {
            try
            {
                if (ShuffleIcon == null || ShuffleBorder == null) return;
                if (_isShuffled)
                {
                    ShuffleIcon.Glyph = "\uE14C";
                    ShuffleIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 245, 255));
                    ShuffleBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 245, 255));
                }
                else
                {
                    ShuffleIcon.Glyph = "\uE14C";
                    ShuffleIcon.Foreground = new SolidColorBrush(Colors.Gray);
                    ShuffleBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(77, 128, 128, 128));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApplyShuffleUI error: {ex.Message}");
            }
        }

        private void BtnPrev_Click(object sender, RoutedEventArgs e) => PlayPrevious();
        private void BtnNext_Click(object sender, RoutedEventArgs e) => PlayNext();

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentSong == null && _filteredSongs.Count > 0)
                {
                    if (_isShuffled) { if (_shuffledOrder.Count == 0) RebuildShuffleOrder(); _shuffleIndex = 0; PlaySong(_shuffledOrder[0]); }
                    else PlaySong(_filteredSongs[0]);
                    return;
                }

                if (_isPlaying)
                {
                    _isPlaying = false;
                    _progressTimer.Stop();
                    StopPulse();
                    _mediaPlayer.Pause();
                    StopPlayingAnimation();
                    UpdatePlayPauseButton();
                    UpdateSMTCState(false);
                }
                else
                {
                    _isPlaying = true;
                    _progressTimer.Start();
                    StartPulse();
                    if (!string.IsNullOrEmpty(_currentSong?.FilePath)) _mediaPlayer.Play();
                    UpdatePlayPauseButton();
                    int idx = _filteredSongs.IndexOf(_currentSong);
                    UpdatePlayingItemBorder(idx, true);
                    UpdateSMTCState(true);
                }
                SaveSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BtnPlayPause_Click error: {ex.Message}");
            }
        }

        private void BtnRepeat_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _repeatMode = _repeatMode switch
                {
                    RepeatMode.None => RepeatMode.All,
                    RepeatMode.All => RepeatMode.One,
                    _ => RepeatMode.None
                };
                ApplyRepeatUI();
                SaveSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BtnRepeat_Click error: {ex.Message}");
            }
        }

        private void ApplyRepeatUI()
        {
            try
            {
                if (RepeatIcon == null || RepeatBorder == null || BtnRepeat == null) return;
                switch (_repeatMode)
                {
                    case RepeatMode.All:
                        RepeatIcon.Glyph = "\uE8EE";
                        RepeatIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 245, 255));
                        RepeatBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 245, 255));
                        ToolTipService.SetToolTip(BtnRepeat, "Repeat All");
                        break;
                    case RepeatMode.One:
                        RepeatIcon.Glyph = "\uE8ED";
                        RepeatIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 168, 85, 247));
                        RepeatBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 168, 85, 247));
                        ToolTipService.SetToolTip(BtnRepeat, "Repeat One");
                        break;
                    default:
                        RepeatIcon.Glyph = "\uE8EE";
                        RepeatIcon.Foreground = new SolidColorBrush(Colors.Gray);
                        RepeatBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(77, 128, 128, 128));
                        ToolTipService.SetToolTip(BtnRepeat, "Repeat");
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApplyRepeatUI error: {ex.Message}");
            }
        }

        private void StartPlayingAnimation(Border border)
        {
            try
            {
                var gradientBrush = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1)
                };
                gradientBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0x00, 0xF5, 0xFF), Offset = 0.0 });
                gradientBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7), Offset = 0.5 });
                gradientBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0xEC, 0x48, 0x99), Offset = 1.0 });

                border.BorderBrush = gradientBrush;
                border.BorderThickness = new Thickness(1.5);

                double phase = 0;
                var animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(7) };

                animTimer.Tick += (_, _) =>
                {
                    try
                    {
                        if (_currentPlayingBorder != border) { animTimer.Stop(); return; }
                        phase += 0.40;
                        if (phase > Math.PI * 2) phase -= Math.PI * 2;

                        double t = (Math.Sin(phase) + 1.0) / 2.0;
                        double t2 = (Math.Sin(phase + 2.094) + 1.0) / 2.0;
                        double t3 = (Math.Sin(phase + 4.189) + 1.0) / 2.0;

                        byte r1 = (byte)(0x00 + t * (0xA8 - 0x00));
                        byte g1 = (byte)(0xF5 + t * (0x55 - 0xF5));
                        byte b1 = (byte)(0xFF + t * (0xF7 - 0xFF));

                        byte r2 = (byte)(0xA8 + t2 * (0xEC - 0xA8));
                        byte g2 = (byte)(0x55 + t2 * (0x48 - 0x55));
                        byte b2 = (byte)(0xF7 + t2 * (0x99 - 0xF7));

                        byte r3 = (byte)(0xEC + t3 * (0x00 - 0xEC));
                        byte g3 = (byte)(0x48 + t3 * (0xF5 - 0x48));
                        byte b3 = (byte)(0x99 + t3 * (0xFF - 0x99));

                        if (border.BorderBrush is LinearGradientBrush brush && brush.GradientStops.Count == 3)
                        {
                            brush.GradientStops[0].Color = Color.FromArgb(0xFF, r1, g1, b1);
                            brush.GradientStops[1].Color = Color.FromArgb(0xFF, r2, g2, b2);
                            brush.GradientStops[2].Color = Color.FromArgb(0xFF, r3, g3, b3);
                        }
                    }
                    catch { animTimer.Stop(); }
                };

                _playingAnimTimer?.Stop();
                _playingAnimTimer = animTimer;
                animTimer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"StartPlayingAnimation error: {ex.Message}");
            }
        }

        private void UpdatePlayingItemBorder(int playingIndex, bool isPlaying)
        {
            StopPlayingAnimation();
            if (!isPlaying || playingIndex < 0) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var container = PlaylistView.ContainerFromIndex(playingIndex) as ListViewItem;
                    if (container == null) return;

                    var border = FindVisualChild<Border>(container, "TrackRowBorder");
                    if (border == null) return;

                    _currentPlayingBorder = border;
                    StartPlayingAnimation(border);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UpdatePlayingItemBorder error: {ex.Message}");
                }
            });
        }

        private void StopPlayingAnimation()
        {
            try
            {
                _playingAnimTimer?.Stop();
                _playingAnimTimer = null;

                _playingStoryboard?.Stop();
                _playingStoryboard = null;

                if (_currentPlayingBorder != null)
                {
                    var defaultBrush = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0),
                        EndPoint = new Windows.Foundation.Point(1, 1)
                    };
                    defaultBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x35, 0x00, 0xF5, 0xFF), Offset = 0.0 });
                    defaultBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x25, 0xA8, 0x55, 0xF7), Offset = 0.5 });
                    defaultBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x15, 0xEC, 0x48, 0x99), Offset = 1.0 });

                    _currentPlayingBorder.BorderBrush = defaultBrush;
                    _currentPlayingBorder.BorderThickness = new Thickness(1);
                    _currentPlayingBorder.Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
                    _currentPlayingBorder = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"StopPlayingAnimation error: {ex.Message}");
            }
        }

        private T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            try
            {
                int count = VisualTreeHelper.GetChildrenCount(parent);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is T fe && fe.Name == name) return fe;
                    var result = FindVisualChild<T>(child, name);
                    if (result != null) return result;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VisualTree error: {ex.Message}");
            }
            return null;
        }

        private void StartPlayButtonAnimation()
        {
            try
            {
                var gradientBrush = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1)
                };
                gradientBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0x00, 0xF5, 0xFF), Offset = 0.0 });
                gradientBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7), Offset = 0.5 });
                gradientBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0xEC, 0x48, 0x99), Offset = 1.0 });

                PlayBtnBorder.BorderBrush = gradientBrush;
                PlayBtnBorder.BorderThickness = new Thickness(2.8);

                double phase = 0;
                var animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

                animTimer.Tick += (_, _) =>
                {
                    try
                    {
                        phase += 0.20;
                        if (phase > Math.PI * 2) phase -= Math.PI * 2;

                        double t = (Math.Sin(phase) + 1.0) / 2.0;
                        double t2 = (Math.Sin(phase + 2.094) + 1.0) / 2.0;
                        double t3 = (Math.Sin(phase + 4.189) + 1.0) / 2.0;

                        byte r1 = (byte)(0x00 + t * (0xA8 - 0x00));
                        byte g1 = (byte)(0xF5 + t * (0x55 - 0xF5));
                        byte b1 = (byte)(0xFF + t * (0xF7 - 0xFF));

                        byte r2 = (byte)(0xA8 + t2 * (0xEC - 0xA8));
                        byte g2 = (byte)(0x55 + t2 * (0x48 - 0x55));
                        byte b2 = (byte)(0xF7 + t2 * (0x99 - 0xF7));

                        byte r3 = (byte)(0xEC + t3 * (0x00 - 0xEC));
                        byte g3 = (byte)(0x48 + t3 * (0xF5 - 0x48));
                        byte b3 = (byte)(0x99 + t3 * (0xFF - 0x99));

                        if (PlayBtnBorder.BorderBrush is LinearGradientBrush brush && brush.GradientStops.Count == 3)
                        {
                            brush.GradientStops[0].Color = Color.FromArgb(0xFF, r1, g1, b1);
                            brush.GradientStops[1].Color = Color.FromArgb(0xFF, r2, g2, b2);
                            brush.GradientStops[2].Color = Color.FromArgb(0xFF, r3, g3, b3);
                        }
                    }
                    catch { animTimer.Stop(); }
                };

                _playBtnAnimTimer?.Stop();
                _playBtnAnimTimer = animTimer;
                animTimer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"StartPlayButtonAnimation error: {ex.Message}");
            }
        }

        private void StopPlayButtonAnimation()
        {
            try
            {
                _playBtnAnimTimer?.Stop();
                _playBtnAnimTimer = null;
                PlayBtnBorder.BorderBrush = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(0x00, 0x00, 0xF5, 0xFF), Offset = 0.0 },
                        new GradientStop { Color = Color.FromArgb(0x00, 0xA8, 0x55, 0xF7), Offset = 1.0 }
                    }
                };
                PlayBtnBorder.BorderThickness = new Thickness(2);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"StopPlayButtonAnimation error: {ex.Message}");
            }
        }

        private void BtnVolume_Click(object sender, RoutedEventArgs e) { _isMuted = !_isMuted; UpdateVolumeUI(); SaveSettings(); }

        private void TrkSpeed_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (!_isInitialized || LblSpeedDisplay == null) return;
                _playbackRate = TrkSpeed.Value;
                LblSpeedDisplay.Text = $"{_playbackRate:F1}x";
                _mediaPlayer.PlaybackRate = _playbackRate;
                SaveSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TrkSpeed_ValueChanged error: {ex.Message}");
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (_allSongs == null) return;
                RefreshFilteredList(TxtSearch.Text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TxtSearch_TextChanged error: {ex.Message}");
            }
        }

        private void PlaylistView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (PlaylistView.SelectedItem is Song song) PlaySong(song);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PlaylistView_SelectionChanged error: {ex.Message}");
            }
        }

        private void ProgressBar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            try
            {
                if (ProgressHitTarget.ActualWidth <= 0) return;
                var pos = e.GetPosition(ProgressHitTarget);
                double pct = Math.Max(0, Math.Min(100, pos.X / ProgressHitTarget.ActualWidth * 100));
                SeekToPercent(pct);
                UpdateProgressUI(pct, TimeSpan.FromSeconds((_currentSong?.DurationInSeconds ?? 0) * pct / 100.0), TimeSpan.FromSeconds(_currentSong?.DurationInSeconds ?? 0));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ProgressBar_Tapped error: {ex.Message}");
            }
        }

        private void ProgressBar_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            try
            {
                if (ProgressHitTarget.ActualWidth <= 0) return;
                _isSeeking = true;
                double fill = Math.Max(0, Math.Min(ProgressHitTarget.ActualWidth, ProgressFill.Width + e.Delta.Translation.X));
                double pct = fill / ProgressHitTarget.ActualWidth * 100.0;
                SeekToPercent(pct);
                UpdateProgressUI(pct, TimeSpan.FromSeconds((_currentSong?.DurationInSeconds ?? 0) * pct / 100.0), TimeSpan.FromSeconds(_currentSong?.DurationInSeconds ?? 0));
                _isSeeking = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ProgressBar_ManipulationDelta error: {ex.Message}");
                _isSeeking = false;
            }
        }

        private void VolumeBar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            try
            {
                double barW = VolumeBarGrid?.Width ?? 110;
                _currentVolume = Math.Max(0, Math.Min(1, e.GetPosition((UIElement)sender).X / barW));
                _isMuted = false; UpdateVolumeUI(); SaveSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VolumeBar_Tapped error: {ex.Message}");
            }
        }

        private void VolumeBar_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            try
            {
                double barW = VolumeBarGrid?.Width ?? 110;
                _currentVolume = Math.Max(0, Math.Min(barW, VolumeFill.Width + e.Delta.Translation.X)) / barW;
                _isMuted = false; UpdateVolumeUI(); SaveSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VolumeBar_ManipulationDelta error: {ex.Message}");
            }
        }

        private void ShowTemporaryStatus(string message)
        {
            try
            {
                var status = LblScanStatus;
                if (status != null)
                {
                    status.Visibility = Visibility.Visible;
                    status.Text = message;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    timer.Tick += (s, _) => { status.Visibility = Visibility.Collapsed; timer.Stop(); };
                    timer.Start();
                }
            }
            catch { }
        }
    }

    public sealed partial class Song : INotifyPropertyChanged
    {
        private string _title = "", _artist = "", _duration = "", _filePath = "";

        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
        public string Artist { get => _artist; set { _artist = value; OnPropertyChanged(); } }
        public string FilePath { get => _filePath; set { _filePath = value; OnPropertyChanged(); } }

        public string Duration
        {
            get => _duration;
            set
            {
                _duration = value;
                var p = value.Split(':');
                if (p.Length == 2 && int.TryParse(p[0], out int m) && int.TryParse(p[1], out int s))
                    DurationInSeconds = m * 60 + s;
                OnPropertyChanged();
            }
        }

        public int DurationInSeconds { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public enum RepeatMode { None, All, One }
}