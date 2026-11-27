using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectricalTestingMachineMain.Service;
using ElectricalTestingMachineMain.Service.LocalDataManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace ElectricalTestingMachineMain.ViewModel.MainLayout
{
    public partial class TitleViewModel:ObservableObject
    {
    }

    public partial class TitleViewModel
    {
        [RelayCommand]
        public void Close()
        {
            if (HandyControl.Controls.MessageBox.Show("是否确定关闭并保存数据?", "退出确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                //保存数据
                LocalDataManager.Instance.WriteAll();
                SaveResultService.Instance.SaveRecord();
                App.Current.Shutdown();
 
            }
            
        }
        [RelayCommand]
        public void Mini()
        {
            App.Current.Dispatcher.Invoke(() => {
                App.Current.MainWindow.WindowState = System.Windows.WindowState.Minimized;
            });

        }

        [RelayCommand]
        public void Max()
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                if (App.Current.MainWindow.WindowState == System.Windows.WindowState.Maximized)
                {
                    App.Current.MainWindow.WindowState = System.Windows.WindowState.Normal;
                }
                else
                {
                    App.Current.MainWindow.WindowState = System.Windows.WindowState.Maximized;
                }
            });
        }
    
}
}
