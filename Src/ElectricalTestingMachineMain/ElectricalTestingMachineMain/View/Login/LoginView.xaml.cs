using ElectricalTestingMachineMain.ViewModel.Login;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ElectricalTestingMachineMain.View.Login
{
    /// <summary>
    /// LoginView.xaml 的交互逻辑
    /// </summary>
    public partial class LoginView : Window
    {
        public LoginView()
        {
            InitializeComponent();
        }

        private void RoleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PasswordPanel == null) return;
            PasswordPanel.Visibility = RoleComboBox.SelectedIndex == 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as LoginViewModel;
            if (vm == null) return;

            // 同步角色选择到 ViewModel
            vm.SelectedRoleIndex = RoleComboBox.SelectedIndex;

            if (RoleComboBox.SelectedIndex == 0)
            {
                // Operator
                vm.LoginCommand.Execute(null);
            }
            else
            {
                // Engineer - pass password from PasswordBox
                vm.Password = PasswordBox.Password;
                vm.LoginCommand.Execute(null);
            }

            if (vm.DialogResult == true)
            {
                DialogResult = true;
                Close();
            }
            else if (vm.IsErrorVisible)
            {
                ErrorText.Text = vm.ErrorMessage;
                ErrorText.Visibility = Visibility.Visible;
            }
        }
    }
}
