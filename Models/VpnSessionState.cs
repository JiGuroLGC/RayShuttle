using System;

namespace RayShuttle.Models
{
    /// <summary>
    /// 跨页面共享的「当前选中节点」。
    ///
    /// 节点是登录后从远端拉取的，**拉取失败时列表为空、选中项为 null**——
    /// 这一点必须让上层显式处理，不能靠一个内置的默认节点兜底：
    /// 那样会让用户看到并不存在的节点，还会误以为账号是好的。
    /// </summary>
    public static class VpnSessionState
    {
        private static ProxyNode? _currentNode;

        /// <summary>当前选中的节点发生变化时触发（含变为 null）。</summary>
        public static event EventHandler? CurrentNodeChanged;

        /// <summary>当前选中的节点。没有可用节点时为 null。</summary>
        public static ProxyNode? CurrentNode
        {
            get => _currentNode;
            set
            {
                if (ReferenceEquals(_currentNode, value))
                {
                    return;
                }

                _currentNode = value;
                CurrentNodeChanged?.Invoke(null, EventArgs.Empty);
            }
        }
    }
}
