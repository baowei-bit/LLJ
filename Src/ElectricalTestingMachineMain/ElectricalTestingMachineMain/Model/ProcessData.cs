using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElectricalTestingMachineMain.Model
{
    public class ProcessData
    {
        [DisplayName("工位")]
        public string? StationName { get; set; }
        [DisplayName("SN")]
        public string? SerialNumber { get; set; }
        /// <summary>
        /// 配方名称
        /// </summary>
        [DisplayName("配方名称")]
        public string? FormulaHeader {  get; set; }
        /// <summary>
        /// 100N时长度
        /// </summary>
        [DisplayName("100N时长度")]
        public double LengthAt100N { get; set; }
        /// <summary>
        /// 开始时间
        /// </summary>
        [DisplayName("开始时间")]
        public DateTime? StartTime {  get; set; }
        [DisplayName("结束时间")]
        public DateTime? EndTime { get; set; }

        [DisplayName("最大拉力")]
        public double MaxForce {  get; set; }
        /// <summary>
        /// 测试时间，秒
        /// </summary>
        [DisplayName("测试时间（s）")]
        public double CtTime {  get; set; }
        /// <summary>
        /// 运行结果
        /// </summary>
        [DisplayName("运行结果")]
        public string? Result {  get; set; }
    }
}
