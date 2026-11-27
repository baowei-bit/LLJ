using ElectricalTestingMachineMain.Model.Enum;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Model.Messenger
{
    /// <summary>
    /// 工作流变更数据
    /// </summary>
    public record class WorkStatusMessenger
    {
        public Workflow.WorkStatus Status { get; set; }
        public string? Message { get; set; }
    }

    public record class SwitchViewMessenger
    {
        public SwitchViewType type { get; set; }
    }
}
