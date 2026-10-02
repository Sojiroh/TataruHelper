using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using FFXIVTataruHelper.EventArguments;
using FFXIVTataruHelper.Services.GameMemory;
using FFXIVTataruHelper.Services.Logging;
using FFXIVTataruHelper.Services.Settings;
using FFXIVTataruHelper.Services.UI;
using FFXIVTataruHelper.TataruComponentModel;
using FFXIVTataruHelper.ViewModel;

using Translation.Models;

namespace FFXIVTataruHelper
{
    public partial class ChatTabView : UserControl, IDisposable
    {
        private readonly ChatProcessor _processor;
        private readonly ISettingsStore _settings;
        private readonly IUiDispatcher _dispatcher;
        private readonly IAppLogger _logger;
        private readonly ChatMessageParagraphBuilder _paragraphBuilder;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly CancellationToken _cancellationToken;
        private readonly HashSet<string> _reportedFailureEngines = new HashSet<string>(StringComparer.Ordinal);
        private bool _disposed;

        public ChatWindowViewModel ViewModel { get; }

        public event Action<ChatTabView, string, string, string> MessageDisplayed;

        public ChatTabView(ChatWindowViewModel viewModel, ChatProcessor processor, ISettingsStore settings,
            IUiDispatcher dispatcher, IAppLogger logger, GameIconReader gameIcons)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = viewModel;
            _processor = processor;
            _settings = settings;
            _dispatcher = dispatcher;
            _logger = logger;
            _paragraphBuilder = new ChatMessageParagraphBuilder(viewModel, gameIcons);
            _cancellationToken = _lifetime.Token;
            ChatRtb.Padding = new Thickness(viewModel.ContentPadding);
            viewModel.AsyncPropertyChanged += OnSettingsChanged;
            viewModel.RequestChatClear += OnClearRequested;
            _processor.TextArrived += OnTextArrived;
        }

        public void ClearChat()
        {
            ChatRtb.Document.Blocks.Clear();
        }

        private async Task OnClearRequested(TatruEventArgs args)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                    ClearChat();
            });
        }

        private async Task OnTextArrived(ChatMessageArrivedEventArgs args)
        {
            if (_cancellationToken.IsCancellationRequested)
                return;

            var chatCode = ViewModel.ChatCodes.FirstOrDefault(code => code.Code == args.ChatMessage.Code);
            if (chatCode == null || !chatCode.IsChecked)
                return;

            var color = chatCode.Color;
            var text = string.Empty;
            var speaker = string.Empty;
            if (args.ChatMessage.Text.Length > 0)
            {
                TranslationResult result;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken))
                {
                    timeout.CancelAfter(_settings.TranslatorWaitTimeMs);
                    try
                    {
                        result = await _processor.Translate(args.ChatMessage.Text, ViewModel.SelectedEngine,
                            ViewModel.CurrentTranslateFromLanguage, ViewModel.CurrentTranslateToLanguage,
                            chatCode.Code, timeout.Token);
                    }
                    catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        result = TranslationResult.Failure(ViewModel.SelectedEngine?.EngineName ?? default,
                            TranslationFailureKind.ProviderException, "Translation timed out.");
                    }
                }

                if (_cancellationToken.IsCancellationRequested)
                    return;

                if (!result.IsSuccess)
                {
                    var engineName = ViewModel.SelectedEngine?.Name ?? result.Engine.ToString();
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (!_disposed && _reportedFailureEngines.Add(engineName))
                        {
                            var prefix = (string)Application.Current.Resources["TranslationEngineError"];
                            ShowTranslatedText((prefix ?? "Translation failed:") + " " + engineName, color);
                        }
                    });
                    return;
                }

                text = result.Text;
                speaker = result.SpeakerName;
                await _dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || ViewModel.SelectedEngine == null ||
                        result.Engine == ViewModel.SelectedEngine.EngineName)
                        return;

                    var substitute = ViewModel.AvailableEngines.FirstOrDefault(engine => engine.EngineName == result.Engine);
                    if (substitute != null)
                    {
                        var template = (string)Application.Current.Resources["TranslationEngineSwitched"];
                        text = string.Format(template ?? "[{0} → {1}]", ViewModel.SelectedEngine.Name, substitute.Name)
                               + " " + text;
                        ViewModel.SelectedEngine = substitute;
                    }
                });
            }

            var timestamp = ViewModel.ShowTimestamps ? args.ChatMessage.TimeStamp : default(DateTime);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                    ShowTranslatedText(text, color, speaker, timestamp, args.ChatMessage.Text);
            });
        }

        private void ShowTranslatedText(string text, Color color, string speaker = "",
            DateTime timestamp = default(DateTime), string sourceLine = null)
        {
            try
            {
                text = text.Trim(' ');
                if (ViewModel.ShowOnlyLastMessage)
                    ClearChat();

                ChatRtb.Document.Blocks.Add(_paragraphBuilder.BuildMessageParagraph(text, color, speaker, timestamp));
                TrimMessages(_settings.MaxChatMessages);
                ChatRtb.ScrollToEnd();
                MessageDisplayed?.Invoke(this, speaker, text, sourceLine);
            }
            catch (Exception exception)
            {
                _logger.WriteLog(exception);
            }
        }

        private void TrimMessages(int maximum)
        {
            if (maximum <= 0)
                return;

            while (ChatRtb.Document.Blocks.Count > maximum)
                ChatRtb.Document.Blocks.Remove(ChatRtb.Document.Blocks.FirstBlock);
        }

        private async Task OnSettingsChanged(AsyncPropertyChangedEventArgs args)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed)
                    return;

                switch (args.PropertyName)
                {
                    case nameof(ChatWindowViewModel.ContentPadding):
                        ChatRtb.Padding = new Thickness(ViewModel.ContentPadding);
                        break;
                    case nameof(ChatWindowViewModel.ShowOnlyLastMessage):
                        if (ViewModel.ShowOnlyLastMessage)
                            TrimMessages(1);
                        break;
                    case nameof(ChatWindowViewModel.MessagesInContainer):
                    case nameof(ChatWindowViewModel.MessageContainerPadding):
                    case nameof(ChatWindowViewModel.MessageContainerAlpha):
                    case nameof(ChatWindowViewModel.MessageContainerBorderThickness):
                    case nameof(ChatWindowViewModel.MessageContainerBorderAlpha):
                        foreach (var paragraph in ChatRtb.Document.Blocks.OfType<Paragraph>())
                        {
                            foreach (var inline in paragraph.Inlines)
                            {
                                if (inline is InlineUIContainer container && container.Child is Border border)
                                    _paragraphBuilder.ApplyMessageContainerVisual(border);
                            }
                        }
                        break;
                }
            });
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _processor.TextArrived -= OnTextArrived;
            ViewModel.AsyncPropertyChanged -= OnSettingsChanged;
            ViewModel.RequestChatClear -= OnClearRequested;
            _lifetime.Cancel();
            _lifetime.Dispose();
            MessageDisplayed = null;
        }
    }
}
