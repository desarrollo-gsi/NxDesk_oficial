using NxDesk.Client.Views.WelcomeView.ViewModel;
using NxDesk.Domain.Entities;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;


namespace NxDesk.Client.Views.WelcomeView
{
    public partial class WelcomeViewControl : UserControl
    {
        public WelcomeViewControl()
        {
            InitializeComponent();
        }

        private void DeviceCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is DiscoveredDevice device)
            {
                if (this.DataContext is WelcomeViewModel vm)
                {
                    vm.SelectDevice(device);
                }
            }
        }

        private void CopyId_Click(object sender, RoutedEventArgs e)
        {
            if (this.DataContext is WelcomeViewModel vm && !string.IsNullOrEmpty(vm.MyId))
            {
                Clipboard.SetText(vm.MyId);
            }
        }
    }
}
