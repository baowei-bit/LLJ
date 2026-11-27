using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using ElectricalTestingMachineMain.Model.Enum;
using ElectricalTestingMachineMain.Model.Messenger;
using ElectricalTestingMachineMain.Service;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ElectricalTestingMachineMain.ViewModel.MainLayout
{
    public partial class BottomViewModel:ObservableObject,IRecipient<SwitchViewMessenger>
    {
        #region 显示时间
        private DispatcherTimer? _timer;

        private string currentTime;
        public string CurrentTime
        {
            get => currentTime;
            set => SetProperty(ref currentTime, value);
        }

        #endregion

        private int tabIndex;
        public int TabIndex
        {
            get => tabIndex;
            set
            {
                if (SetProperty(ref tabIndex, value))
                {
                    ShowViewService.SwitchView(value switch
                    {
                        0 => SwitchViewType.Monitoring,
                        1 => SwitchViewType.Configuration,
                        _ => SwitchViewType.Monitoring
                    });
                }
            }
        }

        /// <summary>
        /// 是否为工程师（控制配置页是否可见）
        /// </summary>
        private bool isEngineer = AuthenticationService.Instance.IsEngineer;
        public bool IsEngineer
        {
            get => isEngineer;
            set => SetProperty(ref isEngineer, value);
        }

        private string currentUser = AuthenticationService.Instance.CurrentUserName;
        public string CurrentUser
        {
            get => currentUser;
            set => SetProperty(ref currentUser, value);
        }
    }

    public partial class BottomViewModel
    {
        public BottomViewModel()
        {
            _timer = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += _timer_Tick;
            UpdateTime();
            _timer.Start();

            // 监听权限变更
            AuthenticationService.Instance.RoleChanged += OnRoleChanged;
        }

        private void OnRoleChanged(UserRole role)
        {
            IsEngineer = role == UserRole.Engineer;
            CurrentUser = AuthenticationService.Instance.CurrentUserName;
        }

        private void _timer_Tick(object? sender, EventArgs e)
        {
            UpdateTime();
        }

        private void UpdateTime()
        {
            CurrentTime = DateTime.Now.ToString("yyyy MM dd HH:mm:ss");
        }
        /// <summary>
        /// 如需使用设置isactive=true，注意销毁时取消，容易内存泄露
        /// </summary>
        /// <param name="message"></param>
        public void Receive(SwitchViewMessenger message)
        {
            // 外部切换视图时同步 TabIndex
        }
    }
}
