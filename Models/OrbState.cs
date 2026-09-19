namespace RayShuttle.Models
{
    /// <summary>光球（也就是连接）的状态。</summary>
    public enum OrbState
    {
        /// <summary>未连接。</summary>
        Disconnected,

        /// <summary>正在建立连接。</summary>
        Connecting,

        /// <summary>已连接。</summary>
        Connected
    }
}
