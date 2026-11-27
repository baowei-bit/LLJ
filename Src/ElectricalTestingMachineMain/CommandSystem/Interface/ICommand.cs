using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CommandSystem.Interface
{
    /// <summary>
    /// 指令
    /// </summary>
    public interface ICommand
    {
        /// <summary>
        /// 指令名称
        /// </summary>
        public string CommandName { get;  set; }

        
    }
}
