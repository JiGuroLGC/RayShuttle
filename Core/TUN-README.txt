TUN 全局模式 — 组件占位说明
=============================

TUN 模式的包转发链路：

    TUN 适配器（tun2socks 基于 wintun.dll 创建，适配器名 RayShuttleTun）
      → tun2socks.exe（gVisor 网络栈：把 IP 包转成 TCP/UDP 流）
        → Xray SOCKS 入站（127.0.0.1:<SocksPort>）
          → 节点

本目录需要放置的组件
--------------------

1. tun2socks.exe（必需，当前**尚未内置**，属于第三方组件占位）

   - 项目：https://github.com/xjasonlyu/tun2socks （Go，gVisor netstack）
   - 下载：该仓库 GitHub Releases 的 Windows 版本（本应用仅发 Windows x64，取 windows-amd64）
   - 放置：解压出 tun2socks.exe 放到本目录（与 xray.exe 同级）。
   - 依赖：wintun.dll 已随包在本目录，tun2socks 启动时按工作目录加载它。
   - 许可：GPL-3.0。**分发本应用时必须保留本说明并附其源码获取地址**
     （建议同时归因进设置页「关于」），这不影响主程序的 Apache-2.0 许可，
     但属于随包分发的独立作品，归因不可省略。

2. wintun.dll（已随包）

   - 项目：https://www.wintun.net/（MPL-2.0，见同目录 LICENSE-Wintun）。
   - 注意：wintun.dll 是**架构相关**的。当前只随包了一份；若发布多架构，
     每个架构的发布目录都应放对应架构的 wintun.dll（tun2socks 亦按架构下载）。

没有这些文件时会怎样
--------------------

开启 TUN 开关并连接时，连接会失败并在错误弹窗中提示「缺少 TUN 组件」，
系统代理模式完全不受影响。

当前版本的限制（设计上接受的代价）
----------------------------------

- DNS 全部经隧道转发（UDP over SOCKS），域名的分流匹配依赖 Xray sniffing
  （routeOnly），QUIC 类流量的域名还原不完整；
- 助手（RayShuttle.TunHelper.exe）每次应用会话第一次开启 TUN 时弹一次 UAC，
  之后跨连接复用；主程序退出后助手会自动清理退出；
- TUN 模式当前仅支持非打包（自包含发布）方式分发；MSIX 打包布局未包含助手。
