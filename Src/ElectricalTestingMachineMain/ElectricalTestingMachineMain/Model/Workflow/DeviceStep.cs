using HandyControl.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Model.Workflow
{
    public abstract class DeviceStep
    {
        public StepMark _stepMark { get; set; }
        public abstract Task<StepResult> ExcuteStep(ExcuteContext context, CancellationToken token);
    }

    public abstract class InitDeviceStep : DeviceStep
    {
        public string Header { get; set; }

        public abstract ExcuteContext? GetExcuteContext();
        public abstract void StopConfig();
    }
}
