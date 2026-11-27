using CommunityToolkit.Mvvm.Messaging;
using ElectricalTestingMachineMain.Model.Messenger;
using ElectricalTestingMachineMain.Model.Workflow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Service.Workflow
{
    public class WorkEngine
    {
        private static readonly WorkEngine _instance = new Lazy<WorkEngine>(() => new WorkEngine()).Value;

        public static WorkEngine Instance => _instance;
        private InitDeviceStep? _initStep;
        private DeviceStep? _currentStep;
        private CancellationTokenSource? _workCts;
        private ExcuteContext? _context;

        public WorkStatus status { get; set; } = WorkStatus.Free;

        public WorkEngine()
        {
            //_initStep = new TestInitStep();
        }

        #region 工作流控制
        public async void Start()
        {
            if (status == WorkStatus.Run)
            {
                LocalLogger.Instance.Info("工作流已在运行中");
                return;
            }

            _workCts = new CancellationTokenSource();
            _context = _initStep?.GetExcuteContext();
            _currentStep = _initStep;
            status = WorkStatus.Run;

            SendStatusMessage(WorkStatus.Run, "工作流启动");

            try
            {
                while (!_workCts.IsCancellationRequested)
                {
                    if (_currentStep is null)
                    {
                        LocalLogger.Instance.Error("工作流步骤为空，工作流终止");
                        break;
                    }

                    var res = await _currentStep.ExcuteStep(_context, _workCts.Token);
                    if (!res.IsSucces)
                    {
                        string errorMsg = res.ErrorMessage ?? "未知错误";
                        LocalLogger.Instance.Error($"工作流步骤执行失败: {errorMsg}");
                        SendStatusMessage(WorkStatus.Error, $"步骤失败: {errorMsg}");
                        Stop();
                        return;
                    }

                    _context = res.context;
                    _currentStep = res.NextStep;
                }
            }
            catch (OperationCanceledException)
            {
                LocalLogger.Instance.Info("工作流已取消");
            }
            catch (Exception ex)
            {
                LocalLogger.Instance.Error($"工作流异常: {ex.Message}");
                SendStatusMessage(WorkStatus.Error, $"工作流异常: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (_workCts is not null && !_workCts.IsCancellationRequested)
            {
                _workCts.Cancel();
            }

            status = WorkStatus.Free;
            _initStep?.StopConfig();
            SendStatusMessage(WorkStatus.Free, "空闲");
        }

        public void Pause()
        {
            // 预留暂停功能
        }

        public void Resume()
        {
            // 预留恢复功能
        }

        private void SendStatusMessage(WorkStatus workStatus, string message)
        {
            WeakReferenceMessenger.Default.Send(new WorkStatusMessenger
            {
                Status = workStatus,
                Message = message
            });
        }
        #endregion
    }
}
