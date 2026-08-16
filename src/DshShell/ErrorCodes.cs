namespace DshWeb;

/// <summary>
/// 错误码目录（扁平、克制：不做错误框架、不做本地化矩阵）。
/// 所有用户可见错误与结构化日志共用同一套码：弹窗文本含 [E####]、日志 JSON 的
/// code 字段引用、诊断导出按码汇总，便于用户在 Issue 里直接粘贴定位。
/// 约定：E1xxx 运行环境，E2xxx 服务/生命周期，E3xxx 端口/网络，E4xxx 更新/下载，E5xxx 诊断，E9xxx 内部。
/// </summary>
public static class ErrorCodes
{
    public const string E1002 = "E1002"; // 用户拒绝自动安装便携 Node
    public const string E1003 = "E1003"; // 便携 Node 下载失败
    public const string E1004 = "E1004"; // 便携 Node 校验和不匹配
    public const string E1005 = "E1005"; // 便携 Node 解压失败
    public const string E1006 = "E1006"; // WebView2 Runtime 缺失
    public const string E1007 = "E1007"; // 渲染进程反复崩溃，自动重载已停止（保留手动恢复）
    public const string E2001 = "E2001"; // start-dsh.vbs 缺失
    public const string E2002 = "E2002"; // dsh 服务启动超时
    public const string E2003 = "E2003"; // dsh 服务启动日志报错
    public const string E2004 = "E2004"; // dsh 服务不可用（端口无 HTTP 响应）
    public const string E2005 = "E2005"; // 清理僵尸/异常孤儿服务
    public const string E2006 = "E2006"; // 启动已取消（服务可能仍在后台下载/启动）
    public const string E2011 = "E2011"; // 插件缺失，serviceLifetime 配置已忽略并抹除
    public const string E4001 = "E4001"; // dsh 更新下载（npm pack）失败
    public const string E4002 = "E4002"; // dsh 延迟更新应用失败
    public const string E5001 = "E5001"; // 诊断导出失败
    public const string E9001 = "E9001"; // 内部未分类错误

    /// <summary>错误码 → 用户可读的一句话描述（弹窗/日志正文使用）。</summary>
    public static string Describe(string code) => code switch
    {
        E1002 => "已取消自动安装便携 Node.js。",
        E1003 => "便携 Node.js 下载失败（网络或镜像问题）。",
        E1004 => "便携 Node.js 校验和不匹配，已拒绝使用（防供应链篡改）。",
        E1005 => "便携 Node.js 解压失败。",
        E1006 => "缺少 WebView2 Runtime（Edge WebView2），无法渲染窗口。",
        E1007 => "渲染进程反复崩溃，已停止自动重载（可通过托盘唤窗或重新打开恢复）。",
        E2001 => "缺少 start-dsh.vbs，无法自动拉起 dsh 服务。",
        E2002 => "dsh 服务启动超时（下载较慢或网络/代理问题）。",
        E2003 => "dsh 服务启动日志出现错误（npm/权限/依赖问题）。",
        E2004 => "dsh 服务不可用（端口无 HTTP 响应）。",
        E2005 => "检测到上次崩溃遗留的异常服务进程，已清理。",
        E2006 => "启动已取消。若服务仍在后台下载/启动，可稍后重新打开 dsh-launcher。",
        E2011 => "dsh-launcher-lifetime 插件已卸载，已忽略残留的常驻配置并按默认模式运行。",
        E4001 => "dsh 新版本下载失败。",
        E4002 => "dsh 延迟更新应用失败，将继续使用当前版本。",
        E5001 => "诊断日志导出失败。",
        _ => "未分类错误。",
    };
}