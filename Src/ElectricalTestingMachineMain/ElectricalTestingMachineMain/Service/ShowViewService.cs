using CommunityToolkit.Mvvm.Messaging;
using ElectricalTestingMachineMain.Model.Enum;
using ElectricalTestingMachineMain.Model.Messenger;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Service
{
    public class ShowViewService
    {

        public static bool SwitchView(SwitchViewType view)
        {
            if (!CommonConfig(view)) return false;
            //校验 在生产流程中不允许切换页面
            WeakReferenceMessenger.Default.Send(new SwitchViewMessenger() { type = view });
            return true;
        }

        private static bool CommonConfig(SwitchViewType viewType)
        {
            // 权限检查：配置页面需要工程师权限
            if (viewType == SwitchViewType.Configuration && !AuthenticationService.Instance.IsEngineer)
            {
                HandyControl.Controls.MessageBox.Show("需要工程师权限才能访问配置页面", "权限不足", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return false;
            }
            return true;
        }
    }
}
