Xray 内核目录
==============

当前文件
--------
    xray.exe          必需。内核本体。
    wintun.dll        428 KB。当前未使用，为将来的 TUN 全局模式预留。
    LICENSE           必需，不要删。Xray-core 的 MPL 2.0 许可证。
    LICENSE-Wintun    必需（因为随包分发 wintun.dll）。
    README.txt        本文件。

**LICENSE 与 LICENSE-Wintun 不要删。** 随应用分发 Xray 二进制时，MPL 2.0 要求
保留许可证并告知接收方；这是 30 KB 的成本，没有理由省。

已删除的文件（2026-09-19）
--------------------------
    geoip.dat         17.1 MB ┐ 已实测确认当前配置不需要：路由规则用的是显式 CIDR，
    geosite.dat       11.0 MB ┘ 没有引用 geoip:/geosite:，删掉后 `xray run -test` 仍返回 OK。
    README.md                 官方说明文档，运行期不需要。
    xray_no_window.*          官方的无窗口启动脚本（cmd/ps1/vbs），我们用
                              ProcessStartInfo.CreateNoWindow，不需要这些。

**如果将来要给「国内流量直连」加 geosite:cn 这类规则，必须把 geoip.dat 与 geosite.dat
重新下载回来**，否则内核会直接启动失败。

版本
----
    当前：Xray 26.9.9

获取方式：https://github.com/XTLS/Xray-core/releases（下载 windows-64 压缩包）

核对内核可用性
--------------
    Core\xray.exe -version                 查看版本
    Core\xray.exe run -test -c <配置路径>   校验配置是否合法（不启动服务）

**注意 Xray 26.x 已把大量传统协议标记为「已弃用」**（仍可用，但会打印警告，
官方表示后续可能移除）。详见项目根目录 CODEBUDDY.md 的「Xray 版本与协议弃用」一节。
