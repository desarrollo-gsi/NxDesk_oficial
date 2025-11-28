using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NxDesk.Client.ViewModels;
using NxDesk.Client.Views;
using NxDesk.Client.Views.WelcomeView;
using NxDesk.Client.Views.WelcomeView.ViewModel; 

namespace NxDesk.Client
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private WelcomeViewControl _welcomeView;
        private RemoteViewControl _remoteView;

        public MainWindow(MainViewModel mainVm, WelcomeViewModel welcomeVm)
        {
            InitializeComponent();

            _viewModel = mainVm;
            DataContext = _viewModel;

            _welcomeView = new WelcomeViewControl();

            _welcomeView.DataContext = welcomeVm;

            welcomeVm.OnDeviceSelected += (device) =>
            {
                RoomIdTextBox.Text = device.ConnectionID;
                _viewModel.Connect(device.ConnectionID);
            };

            _remoteView = new RemoteViewControl();
            _remoteView.OnInputEvent += (ev) =>
            {
                _viewModel.SendInput(ev.EventType, ev.Key, ev.X, ev.Y, ev.Button, ev.Delta);
            };

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsConnected))
                {
                    UpdateView(_viewModel.IsConnected);
                }
                else if (e.PropertyName == nameof(MainViewModel.CurrentFrame))
                {
                    if (_viewModel.IsConnected && _viewModel.CurrentFrame != null)
                    {
                        _remoteView.SetFrame(_viewModel.CurrentFrame);
                    }
                }
                else if (e.PropertyName == nameof(MainViewModel.RemoteScreens))
                {
                    PopulateScreenMenu();
                }
            };
            UpdateView(false);
        }

        private void UpdateView(bool isConnected)
        {
            if (isConnected)
            {
                ContentArea.Content = _remoteView;
                PreSessionControls.Visibility = Visibility.Collapsed;
                InSessionControls.Visibility = Visibility.Visible;
            }
            else
            {
                ContentArea.Content = _welcomeView;
                PreSessionControls.Visibility = Visibility.Visible;
                InSessionControls.Visibility = Visibility.Collapsed;
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.Connect(RoomIdTextBox.Text);
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.Disconnect();
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            _viewModel.SendInput("keydown", key: e.Key.ToString());
        }

        private void MainWindow_KeyUp(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            _viewModel.SendInput("keyup", key: e.Key.ToString());
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (MenuButton.ContextMenu != null)
            {
                MenuButton.ContextMenu.PlacementTarget = MenuButton;
                MenuButton.ContextMenu.IsOpen = true;
            }
        }

        private void PopulateScreenMenu()
        {
            ScreenContextMenu.Items.Clear();
            if (_viewModel.RemoteScreens == null) return;

            for (int i = 0; i < _viewModel.RemoteScreens.Count; i++)
            {
                int index = i;
                var item = new MenuItem { Header = _viewModel.RemoteScreens[i] };
                item.Click += (s, e) => _viewModel.SwitchScreen(index);
                ScreenContextMenu.Items.Add(item);
            }
        }
    }
}