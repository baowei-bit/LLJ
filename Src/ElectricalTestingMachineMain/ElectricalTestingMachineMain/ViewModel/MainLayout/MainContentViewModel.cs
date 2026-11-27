using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using ElectricalTestingMachineMain.Model.Messenger;
using ElectricalTestingMachineMain.View.Configuration;
using ElectricalTestingMachineMain.View.Monitoring;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace ElectricalTestingMachineMain.ViewModel.MainLayout
{
    public partial class MainContentViewModel :  ObservableRecipient, IRecipient<SwitchViewMessenger>
    {
        [ObservableProperty]
        public UserControl contentSource;

        public void Receive(SwitchViewMessenger message)
        {
            if (message is not null)
            {
                ContentSource = message.type switch
                {
                    Model.Enum.SwitchViewType.Monitoring => _monitoringView,
                    Model.Enum.SwitchViewType.Configuration => new ConfigurationView()
                };
            }
        }
    }

    public partial class MainContentViewModel
    {

        private MonitoringView _monitoringView;
        public MainContentViewModel()
        {
            this.IsActive = true;
            _monitoringView = new MonitoringView();
            ContentSource = _monitoringView;
        }

        public void Dispose()
        {
            this.IsActive = false;
        }
    }
}
