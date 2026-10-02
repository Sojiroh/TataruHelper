using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using FFXIVTataruHelper.EventArguments;
using FFXIVTataruHelper.Services.Logging;
using FFXIVTataruHelper.Services.Settings;
using FFXIVTataruHelper.Services.GameMemory;
using FFXIVTataruHelper.Services.UI;
using FFXIVTataruHelper.TataruComponentModel;
using FFXIVTataruHelper.ViewModel;
using FFXIVTataruHelper.WinUtils;

using Translation.Models;

namespace FFXIVTataruHelper
{
    /// <summary>
    /// Interaction logic for ChatWindow.xaml
    /// </summary>
    public partial class ChatWindow : Window
    {
        public IntPtr WindowHandle
        {
            get { return new WindowInteropHelper(this).Handle; }
        }

        public long WinId
        {
            get { return _ChatWindowViewModel.WinId; }
        }

        private WindowResizer _WindowResizer;

        private WindowClickThroughBehavior _clickThroughBehavior;

        private DateTime _TextArrivedTime;

        private readonly CancellationTokenSource _lifecycleCts =
            new CancellationTokenSource();

        private bool _AutoHidden;

        private readonly List<ChatTabView> _tabs = new List<ChatTabView>();
        private bool _isLoaded;
        private bool _isClosing;
        private bool _switchingTab;
        private bool _syncingRectangle;

        private const uint TopMostSetWindowPosFlags =
            Win32Interfaces.SWP_NOMOVE |
            Win32Interfaces.SWP_NOSIZE |
            Win32Interfaces.SWP_NOACTIVATE |
            Win32Interfaces.SWP_NOOWNERZORDER;

        TataruModel _TataruModel;
        ChatWindowViewModel _ChatWindowViewModel;

        MainWindow _MainWindow;
        readonly IAppLogger _Logger;
        readonly ISettingsStore _SettingsStore;
        readonly IUiDispatcher _UiDispatcher;
        readonly DialogueOverlayHost _dialogueOverlayHost;
        readonly GameIconReader _gameIcons;

        public ChatWindow(
            TataruModel tataruModel,
            ChatWindowViewModel chatWindowViewModel,
            MainWindow mainWindow,
            IAppLogger logger,
            ISettingsStore settingsStore,
            IUiDispatcher uiDispatcher,
            DialogueOverlayHost dialogueOverlayHost,
            GameIconReader gameIcons)
        {
            InitializeComponent();

            try
            {
                _MainWindow = mainWindow;

                _TataruModel = tataruModel;
                _ChatWindowViewModel = chatWindowViewModel;
                _Logger = logger;
                _SettingsStore = settingsStore;
                _UiDispatcher = uiDispatcher;
                _dialogueOverlayHost = dialogueOverlayHost;
                _gameIcons = gameIcons;

                this.DataContext = _ChatWindowViewModel;

                this.ShowInTaskbar = false;


                _TextArrivedTime = DateTime.UtcNow;

                _AutoHidden = false;
                _WindowResizer = new WindowResizer(this, _Logger);
                _clickThroughBehavior = new WindowClickThroughBehavior(this, _Logger);
                AddTab(chatWindowViewModel);
            }
            catch (Exception e)
            {
                _Logger.WriteLog(e);
            }
        }

        #region **UserActions.

        public async Task ClearChat()
        {
            await this.UIThreadAsync(() =>
            {
                SelectedTab?.ClearChat();
            });
        }

        #endregion

        #region **WindowEvents.

        protected virtual void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded)
                return;
            _isLoaded = true;
            AutoHideStatusCheck();

            _TataruModel.FFMemoryReader.AddExclusionWindowHandler((new WindowInteropHelper(this).Handle));

            if (_ChatWindowViewModel.IsClickThrough)
                MakeWindowClickThrough();
            else
                MakeWindowClickable();

            ApplyAlwaysOnTop();

            _TataruModel.FFMemoryReader.AsyncPropertyChanged += OnMemoryReaderPropertyChange;

            _TataruModel.FFMemoryReader.FFWindowStateChanged += OnFFWindowStateChange;

            _dialogueOverlayHost.WantedChanged += OnDialogueOverlayWantedChanged;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _lifecycleCts.Cancel();
            _isClosing = true;
        }

        protected virtual void Window_Deactivated(object sender, EventArgs e)
        {
            int t = 0;
            t++;
        }

        #endregion

        #region **UiEvents.

        public string TabGroupId => _ChatWindowViewModel.TabGroupId;

        private ChatTabView SelectedTab => (ChatTabs.SelectedItem as TabItem)?.Content as ChatTabView;

        public bool ContainsTab(long winId) => _tabs.Any(tab => tab.ViewModel.WinId == winId);

        public void AddTab(ChatWindowViewModel viewModel)
        {
            if (ContainsTab(viewModel.WinId))
                return;

            var shouldSelect = _tabs.Count == 0 || viewModel.IsActiveTab;
            if (_tabs.Count > 0)
                viewModel.ChatWindowRectangle = _ChatWindowViewModel.ChatWindowRectangle;

            var tab = new ChatTabView(viewModel, _TataruModel.ChatProcessor, _SettingsStore,
                _UiDispatcher, _Logger, _gameIcons);
            tab.MessageDisplayed += OnTabMessageDisplayed;
            viewModel.AsyncPropertyChanged += OnSettingsWindowPropertyChange;
            var item = new TabItem { Content = tab, DataContext = viewModel };
            item.SetBinding(HeaderedContentControl.HeaderProperty, new Binding(nameof(ChatWindowViewModel.Name)));
            _tabs.Add(tab);
            ChatTabs.Items.Add(item);
            if (shouldSelect)
                ChatTabs.SelectedItem = item;
        }

        public bool RemoveTab(long winId)
        {
            var tab = _tabs.FirstOrDefault(candidate => candidate.ViewModel.WinId == winId);
            if (tab == null)
                return _tabs.Count == 0;

            tab.ViewModel.AsyncPropertyChanged -= OnSettingsWindowPropertyChange;
            tab.MessageDisplayed -= OnTabMessageDisplayed;
            tab.Dispose();
            _tabs.Remove(tab);
            var item = ChatTabs.Items.Cast<TabItem>().First(candidate => ReferenceEquals(candidate.Content, tab));
            ChatTabs.Items.Remove(item);
            if (_tabs.Count > 0 && ChatTabs.SelectedIndex < 0)
                ChatTabs.SelectedIndex = 0;
            return _tabs.Count == 0;
        }

        private void ChatTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, ChatTabs) || _isClosing || _switchingTab || SelectedTab == null)
                return;

            _switchingTab = true;
            try
            {
                var selected = SelectedTab.ViewModel;
                selected.ChatWindowRectangle = _ChatWindowViewModel.ChatWindowRectangle;
                _ChatWindowViewModel = selected;
                if (_isLoaded)
                {
                    selected.IsWindowVisible = !selected.IsHiddenByUser &&
                        _TataruModel.FFMemoryReader.FFWindowState != WindowState.Minimized &&
                        (selected.IsAlwaysOnTop || _TataruModel.FFMemoryReader.IsGameWindowForeground);
                }
                DataContext = selected;
                foreach (var tab in _tabs)
                    tab.ViewModel.IsActiveTab = ReferenceEquals(tab.ViewModel, selected);
                _TextArrivedTime = DateTime.UtcNow;
                _AutoHidden = false;
                CloseDialogueOverlay();
                if (_isLoaded)
                {
                    if (selected.IsClickThrough)
                        MakeWindowClickThrough();
                    else
                        MakeWindowClickable();
                    ApplyAlwaysOnTop();
                    ShowWindow();
                }
            }
            finally
            {
                _switchingTab = false;
            }
        }

        private void OnTabMessageDisplayed(ChatTabView tab, string speaker, string text, string sourceLine)
        {
            if (_isClosing || !ReferenceEquals(tab, SelectedTab))
                return;

            ShowWindow();
            if (!_ChatWindowViewModel.IsHiddenByUser)
                _TextArrivedTime = DateTime.UtcNow;
            EnsureDialogueOverlay()?.SetLine(speaker, text, sourceLine);
        }

        private void NewTab_Click(object sender, RoutedEventArgs e)
        {
            _TataruModel.TataruViewModel.AddNewChatTab(WinId);
        }

        protected virtual async Task OnSettingsWindowPropertyChange(AsyncPropertyChangedEventArgs ea)
        {
            await this.UIThreadAsync(() =>
            {
                if (_isClosing)
                    return;

                var changed = ea.Sender as ChatWindowViewModel;
                if (ea.PropertyName == nameof(ChatWindowViewModel.ChatWindowRectangle) && !_syncingRectangle)
                {
                    _syncingRectangle = true;
                    try
                    {
                        foreach (var tab in _tabs)
                            tab.ViewModel.ChatWindowRectangle = changed.ChatWindowRectangle;
                    }
                    finally
                    {
                        _syncingRectangle = false;
                    }
                }

                if (!ReferenceEquals(changed, _ChatWindowViewModel))
                {
                    if ((ea.PropertyName == nameof(ChatWindowViewModel.IsActiveTab) && changed.IsActiveTab) ||
                        (ea.PropertyName == nameof(ChatWindowViewModel.IsWindowVisible) && changed.IsWindowVisible))
                    {
                        ChatTabs.SelectedItem = ChatTabs.Items.Cast<TabItem>()
                            .FirstOrDefault(item => ReferenceEquals(item.DataContext, changed));
                    }
                    return;
                }
                switch (ea.PropertyName)
                {
                    case "IsClickThrough":

                        if (_ChatWindowViewModel.IsClickThrough)
                            MakeWindowClickThrough();
                        else
                            MakeWindowClickable();

                        break;
                    case "IsAlwaysOnTop":
                        {
                            ApplyAlwaysOnTop();

                            if (!_ChatWindowViewModel.IsAlwaysOnTop &&
                                !_TataruModel.FFMemoryReader.IsGameWindowForeground)
                            {
                                _ChatWindowViewModel.IsWindowVisible = false;
                            }
                            else if (_ChatWindowViewModel.IsAlwaysOnTop && !_ChatWindowViewModel.IsHiddenByUser)
                            {
                                _ChatWindowViewModel.IsWindowVisible = true;
                                ShowWindow();
                            }
                        }
                        break;
                    case "IsAutoHide":
                        {
                            _TextArrivedTime = DateTime.UtcNow;
                            if (!_ChatWindowViewModel.IsAutoHide)
                                ShowWindow();
                        }
                        break;
                    case "IsWindowVisible":
                        {
                            if (_ChatWindowViewModel.IsWindowVisible == true)
                            {
                                _TextArrivedTime = DateTime.UtcNow;
                                ShowWindow();
                                ApplyAlwaysOnTop();
                            }
                        }
                        break;
                    case "BackGroundColor":
                        {
                            if (_ChatWindowViewModel.IsClickThrough)
                            {
                                MakeWindowClickable();
                                MakeWindowClickThrough();
                            }
                        }
                        break;
                }
            });
        }


        protected virtual async Task OnFFWindowStateChange(WindowStateChangeEventArgs ea)
        {
            await this.UIThreadAsync(() =>
            {
                if (ea.IsRunningNew != ea.IsRunningOld)
                {
                    //_TataruUIModel.IsHiddenByUser = false;

                    _ChatWindowViewModel.IsHiddenByUser = false;

                    //_TextArrivedTime = DateTime.UtcNow;

                    //_AutoHidden = false;
                }

                if (ea.NewWindowState != ea.OldWindowState)
                {
                    if (ea.NewWindowState == WindowState.Minimized)
                    {
                        _ChatWindowViewModel.IsWindowVisible = false;
                    }
                    else if (_ChatWindowViewModel.IsHiddenByUser == false)
                    {
                        if (_AutoHidden == false)
                            _ChatWindowViewModel.IsWindowVisible = true;
                    }
                }
            });
        }

        protected virtual async Task OnMemoryReaderPropertyChange(AsyncPropertyChangedEventArgs ea)
        {
            if (!string.Equals(ea.PropertyName, "IsGameWindowForeground", StringComparison.Ordinal))
                return;

            await this.UIThreadAsync(() =>
            {
                if (_ChatWindowViewModel.IsAlwaysOnTop)
                {
                    ApplyAlwaysOnTop();
                    return;
                }

                if (_TataruModel.FFMemoryReader.IsGameWindowForeground)
                {
                    if (!_ChatWindowViewModel.IsHiddenByUser && !_AutoHidden)
                        _ChatWindowViewModel.IsWindowVisible = true;
                }
                else
                {
                    _ChatWindowViewModel.IsWindowVisible = false;
                }

                ApplyAlwaysOnTop();
            });
        }

        #endregion

        #region **Transaltion.

        private DialogueOverlayWindow _dialogueOverlay;

        /// <summary>
        /// The copy of the game's dialogue box, made the first time there is
        /// something to put in it. Nothing is created while the setting is off,
        /// so it costs a comparison and not a window - and nothing is created
        /// by a window that does not hold the claim, so several chat windows
        /// do not stack several copies over the one box.
        /// </summary>
        private DialogueOverlayWindow EnsureDialogueOverlay()
        {
            if (!_dialogueOverlayHost.IsWanted || !_dialogueOverlayHost.BelongsTo(this))
            {
                return null;
            }

            if (_dialogueOverlay == null)
            {
                _dialogueOverlay = new DialogueOverlayWindow(
                    _TataruModel.FFMemoryReader,
                    () => _TataruModel.FFMemoryReader.GameWindowHandle,
                    _gameIcons);
                _dialogueOverlay.Start();
            }

            return _dialogueOverlay;
        }

        /// <summary>
        /// Takes the copy down and lets it go. Called when the setting is
        /// turned off and when this window closes; the claim is a separate
        /// matter, since a window that is merely not wanted right now is still
        /// the one that would fill the copy if it were.
        /// </summary>
        private void CloseDialogueOverlay()
        {
            if (_dialogueOverlay == null)
            {
                return;
            }

            _dialogueOverlay.Stop();
            _dialogueOverlay.Close();
            _dialogueOverlay = null;
        }

        /// <summary>
        /// The setting was turned on or off. Turned off, the copy comes down
        /// there and then rather than staying over the game until whatever is
        /// said next - which, in a quiet moment, is a long time to look at
        /// something you have just switched off.
        /// </summary>
        private void OnDialogueOverlayWantedChanged(object sender, EventArgs e)
        {
            _UiDispatcher.Invoke(() =>
            {
                if (!_dialogueOverlayHost.IsWanted)
                {
                    CloseDialogueOverlay();
                }
            });
        }


        #endregion

        #region **WindowResize.

        // for each rectangle, assign the following method to its MouseEnter event.
        protected virtual void DisplayResizeCursor(object sender, MouseEventArgs e)
        {
            _WindowResizer.displayResizeCursor(sender);
        }

        protected virtual void DisplayDragCursor(object sender, MouseEventArgs e)
        {
            _WindowResizer.DisplayDragCursor(sender);
        }

        // for each rectangle, assign the following method to its MouseLeave event.
        protected virtual void ResetCursor(object sender, MouseEventArgs e)
        {
            _WindowResizer.resetCursor();
        }

        // for each rectangle, assign the following method to its PreviewMouseDown event.
        protected virtual void Resize(object sender, MouseButtonEventArgs e)
        {
            _WindowResizer.resizeWindow(sender);
        }

        // finally, you may use the following method to enable dragging!
        protected virtual void Drag(object sender, MouseButtonEventArgs e)
        {
            _WindowResizer.dragWindow(sender, e);
        }

        #endregion

        #region **System.

        protected void MakeWindowClickThrough()
        {
            _clickThroughBehavior.MakeClickThrough();
        }

        protected void MakeWindowClickable()
        {
            _clickThroughBehavior.MakeClickable();
        }

        void HideThisWindow_Click(object sender, RoutedEventArgs e)
        {
            _ChatWindowViewModel.IsHiddenByUser = true;

            _ChatWindowViewModel.IsWindowVisible = false;
        }

        void Settings_Click(object sender, RoutedEventArgs e)
        {
            /*
            Helper.Unminimize(_SettigsWindow);

            _SettigsWindow.Visibility = Visibility.Visible;
            _SettigsWindow.Activate();
            _SettigsWindow.Focus();//*/
            _MainWindow.ShowSettingsWindow();
        }

        void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void ApplyAlwaysOnTop()
        {
            try
            {
                this.UIThread(() =>
                {
                    bool isAlwaysOnTop = _ChatWindowViewModel.IsAlwaysOnTop ||
                                         _TataruModel.FFMemoryReader.IsGameWindowForeground;
                    this.Topmost = isAlwaysOnTop;

                    var thisHandle = new WindowInteropHelper(this).Handle;
                    if (thisHandle == IntPtr.Zero)
                        return;

                    ApplyTopMostToHandle(thisHandle, isAlwaysOnTop);

                    // WPF can create a hidden owner when ShowInTaskbar=false.
                    // Keep that owner in the same topmost band as this window.
                    var hiddenOwnerHandle = Win32Interfaces.GetWindow(thisHandle, Win32Interfaces.GW_OWNER);
                    if (hiddenOwnerHandle != IntPtr.Zero)
                    {
                        ApplyTopMostToHandle(hiddenOwnerHandle, isAlwaysOnTop);
                    }
                });
            }
            catch (Exception e)
            {
                _Logger.WriteLog(Convert.ToString(e));
            }
        }

        private static void ApplyTopMostToHandle(IntPtr handle, bool isAlwaysOnTop)
        {
            var insertAfter = isAlwaysOnTop
                ? Win32Interfaces.HWND_TOPMOST
                : Win32Interfaces.HWND_NOTOPMOST;

            Win32Interfaces.SetWindowPos(
                handle,
                insertAfter,
                0,
                0,
                0,
                0,
                TopMostSetWindowPosFlags);
        }

        private void ShowWindow()
        {
            if (_ChatWindowViewModel.IsHiddenByUser == false)
            {
                if (_TataruModel.FFMemoryReader.FFWindowState == WindowState.Minimized)
                    return;

                if (!_ChatWindowViewModel.IsAlwaysOnTop &&
                    !_TataruModel.FFMemoryReader.IsGameWindowForeground)
                {
                    return;
                }

                if (this.WindowState == WindowState.Minimized)
                    this.WindowState = WindowState.Normal;

                if (this.Visibility != Visibility.Visible)
                    this.Show();

                ApplyAlwaysOnTop();
            }
        }

        protected virtual void AutoHideStatusCheck()
        {
            var cancellationToken = _lifecycleCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (_ChatWindowViewModel.IsAutoHide)
                        {
                            var ts = DateTime.UtcNow - _TextArrivedTime;
                            if (ts > _ChatWindowViewModel.AutoHideTimeout)
                            {
                                this.UIThread(() =>
                                {
                                    _AutoHidden = true;
                                    _ChatWindowViewModel.IsWindowVisible = false;
                                });
                            }
                            else
                                _AutoHidden = false;
                        }
                        else
                            _AutoHidden = false;

                        await Task.Delay(_SettingsStore.AutoHideWatcherDelayMs, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _Logger.WriteLog("AutoHide watcher failed.");
                    _Logger.WriteLog(ex);
                }
            }, cancellationToken);
        }

        protected override void OnClosed(EventArgs e)
        {
            // The copy is its own window, not part of this one: stopping it
            // here is the only thing that takes its timer and its frame off
            // screen when this window goes.
            CloseDialogueOverlay();
            _dialogueOverlayHost.WantedChanged -= OnDialogueOverlayWantedChanged;
            _dialogueOverlayHost.Release(this);

            _TataruModel.FFMemoryReader.AsyncPropertyChanged -= OnMemoryReaderPropertyChange;

            _TataruModel.FFMemoryReader.FFWindowStateChanged -= OnFFWindowStateChange;
            foreach (var tab in _tabs)
            {
                tab.ViewModel.AsyncPropertyChanged -= OnSettingsWindowPropertyChange;
                tab.MessageDisplayed -= OnTabMessageDisplayed;
                tab.Dispose();
            }
            _tabs.Clear();

            _lifecycleCts.Cancel();
            _lifecycleCts.Dispose();

            base.OnClosed(e);
        }

        #endregion
    }
}