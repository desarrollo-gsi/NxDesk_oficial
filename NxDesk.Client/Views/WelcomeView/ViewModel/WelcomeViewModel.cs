using NxDesk.Application.Interfaces;
using NxDesk.Domain.Entities;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace NxDesk.Client.Views.WelcomeView.ViewModel
{
    public class WelcomeViewModel : INotifyPropertyChanged
    {
        public event Action<DiscoveredDevice> OnDeviceSelected;
        public event PropertyChangedEventHandler PropertyChanged;

        private readonly INetworkDiscoveryService _discoveryService;
        private readonly IIdentityService _identityService;

        public string MyId { get; private set; }

        private ObservableCollection<DiscoveredDevice> _discoveredDevices;
        public ObservableCollection<DiscoveredDevice> DiscoveredDevices
        {
            get => _discoveredDevices;
            set { _discoveredDevices = value; OnPropertyChanged(); }
        }

        public WelcomeViewModel(INetworkDiscoveryService discoveryService, IIdentityService identityService)
        {
            _discoveryService = discoveryService;
            _identityService = identityService;

            DiscoveredDevices = new ObservableCollection<DiscoveredDevice>();

            MyId = _identityService.GetMyId();
            string myAlias = _identityService.GetMyAlias();

            Debug.WriteLine($"[VM] ID: {MyId}, Alias: {myAlias}");

            _discoveryService.OnDeviceDiscovered += HandleDeviceDiscovered;
            _discoveryService.Start();
        }

        private void HandleDeviceDiscovered(DiscoveredDevice device)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                bool exists = false;
                foreach (var d in DiscoveredDevices)
                {
                    if (d.ConnectionID == device.ConnectionID)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    DiscoveredDevices.Add(device);
                }
            });
        }

        public void SelectDevice(DiscoveredDevice device)
        {
            OnDeviceSelected?.Invoke(device);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}