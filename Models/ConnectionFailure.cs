namespace RayShuttle.Models
{
    /// <summary>连接失败的分类。决定弹窗的标题、给的线索，以及值不值得给「重试」按钮。</summary>
    public enum FailureKind
    {
        /// <summary>没找到 Xray 内核（Core\xray.exe 不存在）。</summary>
        CoreMissing,

        /// <summary>内核起来了但没能就绪：配置有问题、端口被占、或进程秒退。</summary>
        CoreStartup,

        /// <summary>内核正常，但没能接管系统流量。</summary>
        Interception,

        /// <summary>没有可用的节点配置。</summary>
        NoNodes,

        /// <summary>连接超时。</summary>
        Timeout,

        /// <summary>其余未归类的异常。</summary>
        Unexpected,

        /// <summary>TUN 组件缺失（tun2socks / 提权助手不在期望位置）。不可重试。</summary>
        TunMissing,

        /// <summary>用户在 UAC 上拒绝了管理员权限。可重试（批准即可）。</summary>
        TunElevation,

        /// <summary>TUN 隧道建立失败（适配器、路由、tun2socks 启动等）。可重试。</summary>
        TunAdapter
    }

    /// <summary>
    /// 一次连接失败的结构化描述。
    ///
    /// 从前失败只有一个长字符串（<c>LastError</c>），把「一句话结论」「怎么办」「内核原始输出」
    /// 全糊在一起，首页只能整段铺成红字——既破坏版面，用户也没法把信息交给开发者。
    /// 拆成四段之后：界面上只显示 <see cref="Summary"/> 一行，
    /// 完整内容进诊断弹窗（<c>Views/ErrorDialog</c>），可一键复制。
    /// </summary>
    public sealed class ConnectionFailure
    {
        public ConnectionFailure(
            FailureKind kind,
            string title,
            string summary,
            string suggestion = "",
            string detail = "")
        {
            Kind = kind;
            Title = title;
            Summary = summary;
            Suggestion = suggestion;
            Detail = detail;
        }

        public FailureKind Kind { get; }

        /// <summary>一句话结论，例如「无法接管系统流量」。也是弹窗标题。</summary>
        public string Title { get; }

        /// <summary>给人看的一句话解释，首页那一行显示的就是它。</summary>
        public string Summary { get; }

        /// <summary>怎么办。可能为空。</summary>
        public string Suggestion { get; }

        /// <summary>原始细节：内核输出、系统代理现状、异常堆栈等。只出现在诊断报告里。</summary>
        public string Detail { get; }

        /// <summary>弹窗里的分类标签。</summary>
        public string KindText => Kind switch
        {
            FailureKind.CoreMissing => "缺少内核",
            FailureKind.CoreStartup => "内核启动失败",
            FailureKind.Interception => "流量接管失败",
            FailureKind.NoNodes => "节点不可用",
            FailureKind.Timeout => "连接超时",
            FailureKind.TunMissing => "缺少 TUN 组件",
            FailureKind.TunElevation => "需要管理员权限",
            FailureKind.TunAdapter => "TUN 隧道建立失败",
            _ => "未知错误"
        };

        /// <summary>
        /// 重试有没有意义。内核都不在、没有节点可用、或 TUN 组件压根缺失的时候，
        /// 给个「重试」按钮只会浪费用户时间。
        /// </summary>
        public bool IsRetryable => Kind is not (FailureKind.CoreMissing or FailureKind.NoNodes or FailureKind.TunMissing);
    }
}
