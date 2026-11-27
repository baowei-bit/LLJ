using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectricalTestingMachineMain.Service;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.ViewModel.Login
{
    public partial class LoginViewModel:ObservableObject
    {
        [ObservableProperty]
        private int selectedRoleIndex = 0;

        [ObservableProperty]
        private string password = string.Empty;

        [ObservableProperty]
        private string errorMessage = string.Empty;

        [ObservableProperty]
        private bool isErrorVisible = false;

        [ObservableProperty]
        private bool isPasswordRequired = false;

        public bool? DialogResult { get; set; }

        partial void OnSelectedRoleIndexChanged(int value)
        {
            IsPasswordRequired = value == 1; // 1 = Engineer
            if (!IsPasswordRequired)
            {
                Password = string.Empty;
                ErrorMessage = string.Empty;
                IsErrorVisible = false;
            }
        }

        [RelayCommand]
        public void Login()
        {
            IsErrorVisible = false;

            if (SelectedRoleIndex == 0)
            {
                // Operator login
                AuthenticationService.Instance.LoginAsOperator();
                DialogResult = true;
            }
            else
            {
                // Engineer login
                if (string.IsNullOrWhiteSpace(Password))
                {
                    ErrorMessage = "请输入工程师密码";
                    IsErrorVisible = true;
                    return;
                }

                if (AuthenticationService.Instance.LoginAsEngineer(Password))
                {
                    DialogResult = true;
                }
                else
                {
                    ErrorMessage = "密码错误，请重试";
                    IsErrorVisible = true;
                }
            }
        }
    }
}
