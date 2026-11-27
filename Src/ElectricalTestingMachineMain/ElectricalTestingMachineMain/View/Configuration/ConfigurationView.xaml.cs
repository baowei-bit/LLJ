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
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace ElectricalTestingMachineMain.View.Configuration
{
    /// <summary>
    /// ConfigurationView.xaml 的交互逻辑
    /// </summary>
    public partial class ConfigurationView : UserControl
    {
        public ConfigurationView()
        {
            InitializeComponent();
            Loaded += (_, _) => Model.ConfigData.Instance.PropertyChanged += RuntimeConfigChanged;
            Unloaded += (_, _) => Model.ConfigData.Instance.PropertyChanged -= RuntimeConfigChanged;
        }

        private void RuntimeConfigChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (DataContext is not ViewModel.Configuration.ConfigurationViewModel vm) return;
            var current = Model.ConfigData.Instance;
            if (e.PropertyName == nameof(Model.ConfigData.ScanMode)) vm.Config.ScanMode = current.ScanMode;
            if (e.PropertyName == nameof(Model.ConfigData.MesEnabled)) vm.Config.MesEnabled = current.MesEnabled;
        }

        private bool _loadingMesSecret;

        private void LoadMesSecret(PasswordBox box)
        {
            _loadingMesSecret = true;
            try
            {
                box.Password = (box.DataContext as ViewModel.Configuration.ConfigurationViewModel)?.Config.MesPassword ?? string.Empty;
            }
            finally { _loadingMesSecret = false; }
        }

        private void MesSecretBox_Loaded(object sender, RoutedEventArgs e) => LoadMesSecret((PasswordBox)sender);

        private void MesSecretBox_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => LoadMesSecret((PasswordBox)sender);

        private void MesSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_loadingMesSecret && ((PasswordBox)sender).DataContext is ViewModel.Configuration.ConfigurationViewModel vm)
                vm.Config.MesPassword = ((PasswordBox)sender).Password;
        }
    }
}
