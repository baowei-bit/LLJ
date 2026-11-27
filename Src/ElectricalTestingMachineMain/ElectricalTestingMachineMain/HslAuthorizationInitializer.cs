using System.Runtime.CompilerServices;

namespace ElectricalTestingMachineMain;

internal static class HslAuthorizationInitializer
{
    private const string AuthorizationCode = "8DD6AF0645C298BA08265C896D9351A3C71299325B65D019";

    [ModuleInitializer]
    internal static void Activate()
    {
        try
        {
            HslCommunication.Authorization.SetAuthorizationCode(AuthorizationCode);
        }
        catch
        {
            // 授权调用异常不阻断启动；PLC 读写错误由现有通讯日志显示。
        }
    }
}
