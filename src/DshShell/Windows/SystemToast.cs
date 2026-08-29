using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DshWeb.Windows;

/// <summary>
/// 系统 Toast 通知（v0.4.2 重写互操作层）：经 combase 手写的最小 WinRT 互操作
/// 直接调用 Windows.UI.Notifications，不再依赖 24MB 的 Windows SDK 投影程序集
/// （v0.4.1 曾因 windows10.0.19041 TFM 把单文件体积从 1M 推到 26M，并把最低系统
/// 静默抬到 Win10 2004）。
/// - AUMID "dsh-launcher" 首次使用时注册（DisplayName + 可选 app.ico 图标）；
/// - TryShow 绝不抛出：任何失败（无 Appx 感知/注册表拒绝/旧系统缺 API 等）降级
///   Warn 并返回 false，调用方据此记日志；
/// - XML 内容由 ShellLogic.ToastPolicy.BuildToastXml 统一构造（转义/长度策略单点收口）。
///
/// 互操作说明（vtable 均经投影程序集反射核对）：
/// - WinRT 接口槽位 = IUnknown(3) + IInspectable(3) + 接口方法（元数据顺序）；
///   IInspectable 三个占位槽仅用于槽位偏移，本方从不调用。
/// - 已知行为差异：ExpirationTime 暂不设置（需要 IReference&lt;DateTimeOffset&gt; 的
///   完整 IPropertyValue CCW，成本/收益不划算）——通知在操作中心保留到用户清除，
///   视觉弹出时长仍由系统默认策略控制。
/// </summary>
internal static class SystemToast
{
    private static bool _aumidEnsured;
    private static int _roInitTried;

    // ---- combase 入口 ------------------------------------------------------

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true, PreserveSig = false)]
    private static extern void RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true, PreserveSig = false)]
    private static extern void RoActivateInstance(nint activatableClassId, out nint instance);

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true, PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string source, int length, out nint hstring);

    [DllImport("combase.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern void WindowsDeleteString(nint hstring);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int RoInitialize(int initType);

    private static nint H(string s)
    {
        WindowsCreateString(s, s.Length, out var h);
        return h;
    }

    /// <summary>UI 线程已被 COM 初始化（STA/OleInitialize）；RoInitialize 仅兜底，
    /// 任何失败（含 RPC_E_CHANGED_MODE）都不影响后续工厂调用，故忽略返回值。</summary>
    private static void EnsureRoInitialized()
    {
        if (Interlocked.Exchange(ref _roInitTried, 1) != 0) return;
        try { RoInitialize(1 /* RO_INIT_MULTITHREADED */); } catch { /* 见上 */ }
    }

    private static T Factory<T>(string runtimeClassId, Guid iid) where T : class
    {
        EnsureRoInitialized();
        var hClass = H(runtimeClassId);
        try
        {
            RoGetActivationFactory(hClass, ref iid, out var pFactory);
            return (T)Marshal.GetObjectForIUnknown(pFactory);
        }
        finally { WindowsDeleteString(hClass); }
    }

    // ---- 接口声明（槽位序 = 元数据成员序，IInspectable 三占位在前） ----------

    [ComImport, Guid("6CD0E74E-EE65-4489-9EBF-CA43E87BA637"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IXmlDocumentIO
    {
        // IInspectable 占位
        [PreserveSig] int _GetIids(nint a, nint b);
        [PreserveSig] int _GetRuntimeClassName(nint a);
        [PreserveSig] int _GetTrustLevel(nint a);

        void LoadXml(nint xmlHstring);
        void LoadXmlWithSettings(nint xmlHstring, nint loadSettings);
        void SaveToFileAsync(nint a, nint b);
    }

    [ComImport, Guid("04124B20-82C6-4229-B109-FD9ED4662B53"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationFactory
    {
        [PreserveSig] int _GetIids(nint a, nint b);
        [PreserveSig] int _GetRuntimeClassName(nint a);
        [PreserveSig] int _GetTrustLevel(nint a);

        void CreateToastNotification(nint xmlDocument, out nint toast);
    }

    [ComImport, Guid("997E2675-059E-4E60-8B06-1760917C8B80"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotification
    {
        [PreserveSig] int _GetIids(nint a, nint b);
        [PreserveSig] int _GetRuntimeClassName(nint a);
        [PreserveSig] int _GetTrustLevel(nint a);

        void GetContent(out nint xmlDocument);
        void GetExpirationTime(out nint referenceDateTimeOffset);
        void SetExpirationTime(nint referenceDateTimeOffset); // 传 0 = 清除（本方不调用）
        void AddActivated(nint handler, out long token);
        void RemoveActivated(long token);
        void AddDismissed(nint handler, out long token);
        void RemoveDismissed(long token);
        void AddFailed(nint handler, out long token);
        void RemoveFailed(long token);
    }

    [ComImport, Guid("50AC103F-D235-4598-BBEF-98FE4D1A3AD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationManagerStatics
    {
        [PreserveSig] int _GetIids(nint a, nint b);
        [PreserveSig] int _GetRuntimeClassName(nint a);
        [PreserveSig] int _GetTrustLevel(nint a);

        // [2026-08-29 槽位修复] 真实 v1 布局：CreateToastNotification / CreateToastNotifier /
        // GetTemplateContent。旧声明漏了头一个成员且多插了重载占位 → 整体错位一槽，
        // CreateToastNotifier 实际调到 CreateToastNotification（AUMID 指针被当作 XmlDocument）
        // → 0x80070490，toast 自 0.4.1 起从未真正弹出过。
        void CreateToastNotification(nint xmlDocument, out nint toast);
        void CreateToastNotifier(nint aumidHstring, out nint notifier);
        void GetTemplateContent(nint templateType, out nint xmlDocument);
    }

    [ComImport, Guid("75927B93-03F3-41EC-91D3-6E5BAC1B38E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotifier
    {
        [PreserveSig] int _GetIids(nint a, nint b);
        [PreserveSig] int _GetRuntimeClassName(nint a);
        [PreserveSig] int _GetTrustLevel(nint a);

        void Show(nint toast);
        void Hide(nint toast);
        void AddToSchedule(nint scheduledToast);
        void RemoveFromSchedule(nint scheduledToast);
        void GetScheduledToastNotifications(out nint result);
        void GetSetting(out int setting);
    }

    /// <summary>Windows.Foundation.ITypedEventHandler&lt;ToastNotification, Object&gt;
    /// 闭包接口 GUID（pinterface 规范哈希，v5 位已置）。</summary>
    [ComImport, Guid("0671E25B-A9CE-5247-AF38-0E51D23A325D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITypedEventHandlerToastNotificationObject
    {
        [PreserveSig] int _GetIids(nint a, nint b);
        [PreserveSig] int _GetRuntimeClassName(nint a);
        [PreserveSig] int _GetTrustLevel(nint a);

        void Invoke(nint sender, nint args);
    }

    /// <summary>Activated 事件桥：系统线程池回调 → 编组 UI 线程（与原投影版语义一致）。
    /// 实例被静态钉住防止系统持裸指针期间被 GC 回收；触发后自动解除钉住。</summary>
    private sealed class ActivatedBridge : ITypedEventHandlerToastNotificationObject
    {
        private readonly Form? _uiOwner;
        private readonly Action _onClick;

        public ActivatedBridge(Form? uiOwner, Action onClick)
        {
            _uiOwner = uiOwner;
            _onClick = onClick;
            Pinned.Add(this);
        }

        // IInspectable 占位槽（系统侧可能调用；返回 E_NOTIMPL 无碍）
        public int _GetIids(nint a, nint b) => unchecked((int)0x80004001); // E_NOTIMPL
        public int _GetRuntimeClassName(nint a) => unchecked((int)0x80004001);
        public int _GetTrustLevel(nint a) => unchecked((int)0x80004001);

        public void Invoke(nint sender, nint args)
        {
            Pinned.Remove(this);
            try
            {
                if (_uiOwner != null && !_uiOwner.IsDisposed && _uiOwner.IsHandleCreated && _uiOwner.InvokeRequired)
                    _uiOwner.BeginInvoke(_onClick);
                else
                    _onClick();
            }
            catch (Exception ex)
            {
                Logger.Warn("toast click handler failed", ctx: new { error = ex.Message });
            }
        }
    }

    /// <summary>钉住池：有界（32），防 CCW 被提前回收；溢出按 FIFO 释放最旧桥。</summary>
    private static readonly List<ActivatedBridge> Pinned = new();

    // ---- 公共入口 -----------------------------------------------------------

    /// <summary>尽力显示系统 Toast。返回是否成功（失败仅 Warn，绝不抛出）。</summary>
    internal static bool TryShow(Form? uiOwner, string title, string body, TimeSpan expireAfter, Action? onClick)
    {
        // [DSH_TEST_FORCE_TOAST_FAIL=1] 强制走失败分支：验证 NotifyPending 的
        // Toast→托盘气泡→标题驻留 回退链（2026-08-29 通知回归验收通道三）。
        if (string.Equals(Environment.GetEnvironmentVariable("DSH_TEST_FORCE_TOAST_FAIL"), "1",
                StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn("system toast forced-failed (DSH_TEST_FORCE_TOAST_FAIL)");
            return false;
        }
        try
        {
            EnsureAumidRegistered();
            var trace = string.Equals(Environment.GetEnvironmentVariable("DSH_TEST_TOAST"), "1",
                StringComparison.OrdinalIgnoreCase);
            if (trace) Logger.Info("toast step 0: aumid registered");

            // 1) XmlDocument + LoadXml（策略 XML 由 ToastPolicy 单点构造）
            var hXmlDoc = "Windows.Data.Xml.Dom.XmlDocument";
            RoActivateInstance(H(hXmlDoc), out var pDoc);
            if (trace) Logger.Info("toast step 1: xml document activated");
            try
            {
                var docIo = (IXmlDocumentIO)Marshal.GetObjectForIUnknown(pDoc);
                var hXml = H(ShellLogic.ToastPolicy.BuildToastXml(title, body));
                try { docIo.LoadXml(hXml); } finally { WindowsDeleteString(hXml); }
                if (trace) Logger.Info("toast step 1b: loadxml ok");

                // 2) ToastNotificationFactory → new ToastNotification(xml)
                var factory = Factory<IToastNotificationFactory>(
                    "Windows.UI.Notifications.ToastNotification",
                    new Guid("04124B20-82C6-4229-B109-FD9ED4662B53"));
                factory.CreateToastNotification(pDoc, out var pToast);
                if (trace) Logger.Info("toast step 2: notification created");
                try
                {
                    var toast = (IToastNotification)Marshal.GetObjectForIUnknown(pToast);

                    // 3) 点击回调（Activated 事件桥）
                    if (onClick is not null)
                    {
                        var bridge = new ActivatedBridge(uiOwner, onClick);
                        var handlerPtr = Marshal.GetIUnknownForObject(bridge);
                        try { toast.AddActivated(handlerPtr, out _); }
                        finally { Marshal.Release(handlerPtr); }
                    }

                    // 4) ToastNotificationManager.CreateToastNotifier(AUMID).Show(toast)
                    var statics = Factory<IToastNotificationManagerStatics>(
                        "Windows.UI.Notifications.ToastNotificationManager",
                        new Guid("50AC103F-D235-4598-BBEF-98FE4D1A3AD4"));
                    if (trace) Logger.Info("toast step 4a: manager factory ok");
                    var hAumid = H(ShellLogic.ToastPolicy.ToastAumid);
                    try
                    {
                        statics.CreateToastNotifier(hAumid, out var pNotifier);
                        if (trace) Logger.Info("toast step 4b: notifier created");
                        try
                        {
                            var notifier = (IToastNotifier)Marshal.GetObjectForIUnknown(pNotifier);
                            notifier.Show(pToast);
                            if (trace) Logger.Info("toast step 4c: show ok");
                            return true;
                        }
                        finally { Marshal.Release(pNotifier); }
                    }
                    finally { WindowsDeleteString(hAumid); }
                }
                finally { Marshal.Release(pToast); }
            }
            finally { Marshal.Release(pDoc); }
        }
        catch (Exception ex)
        {
            Logger.Warn("system toast failed", ctx: new { error = ex.Message });
            return false;
        }
    }

    /// <summary>首次使用时注册 HKCU AppUserModelId（显示名 + 图标），让 Toast 有身份可挂。</summary>
    private static void EnsureAumidRegistered()
    {
        if (_aumidEnsured) return;
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Classes\AppUserModelId\dsh-launcher"))
        {
            key.SetValue("DisplayName", "dsh-launcher");
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                    key.SetValue("IconUri", new Uri(iconPath).ToString());
            }
            catch { /* 图标可选 */ }
        }
        _aumidEnsured = true;
    }
}
