using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Model.Workflow
{
    /// <summary>
    /// 工序结果
    /// </summary>
    public class StepResult
    {
        public bool IsSucces { get; set; }

        public string? ErrorMessage { get; set; }

        public DeviceStep? NextStep { get; set; }

        public ExcuteContext? context { get; set; }

    }
    /// <summary>
    /// 执行中过程数据
    /// 根据项目填写
    /// </summary>
    public class ExcuteContext
    {

    }
    /// <summary>
    /// 工作步骤
    /// 根据项目填写
    /// </summary>
    public enum StepMark
    {
        
    }
    /// <summary>
    /// 运行状态
    /// </summary>
    public enum WorkStatus
    {
        Free,
        Run,
        Error,
        Paused
    }
}
