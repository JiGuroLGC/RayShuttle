using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RayShuttle.Services
{
    /// <summary>
    /// 设备指纹：64 位十六进制（SHA-256），用于把一个账号绑定到一台机器。
    ///
    /// 素材 = `RSFP1 | MachineGuid | 机器名 | 用户名`，其中 MachineGuid 取自
    /// `HKLM\SOFTWARE\Microsoft\Cryptography`（不需要管理员权限）。
    ///
    /// **刻意不加卷序列号 / 网卡 MAC**：换盘、换网卡、装虚拟机都会让指纹漂移，
    /// 而指纹漂移的后果是账号被判成「多端登录」直接拒绝。稳定性优先于强度——
    /// 反正桌面客户端里的任何东西都能被篡改，指纹只防「随手换台机器用」，不防逆向。
    ///
    /// **后果必须写清楚**：重装系统或换电脑，MachineGuid 会变，账号会一直 403。
    /// 只能由发放方清掉 KV 里的 `boundFingerprint`（见 tools/provision_user.py unbind）。
    /// </summary>
    public static class DeviceFingerprint
    {
        private const string MaterialPrefix = "RSFP1";
        private const string SubKey = @"SOFTWARE\Microsoft\Cryptography";
        private const string ValueName = "MachineGuid";

        private const uint RrfRtRegSz = 0x00000002;

        private static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));

        public static string Get()
        {
            var material = string.Join(
                '|',
                MaterialPrefix,
                ReadMachineGuid(),
                Environment.MachineName,
                Environment.UserName);

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        }

        /// <summary>
        /// 读 MachineGuid。读不到就返回空串 —— 结果仍然稳定，只是区分度下降，
        /// 好过因为一次读取失败导致整个登录流程不可用。
        /// </summary>
        private static string ReadMachineGuid()
        {
            try
            {
                var buffer = new byte[128];
                var size = (uint)buffer.Length;

                // 用 P/Invoke 而不是 Microsoft.Win32.Registry：后者属于 Windows 桌面运行时，
                // 在 WinUI 3 的框架引用下不保证可用。
                if (RegGetValue(HkeyLocalMachine, SubKey, ValueName, RrfRtRegSz, out _, buffer, ref size) != 0)
                {
                    return string.Empty;
                }

                var length = (int)Math.Min(size, (uint)buffer.Length);

                // 返回值带结尾的 '\0'（UTF-16 两个字节），去掉。
                if (length >= 2 && buffer[length - 1] == 0 && buffer[length - 2] == 0)
                {
                    length -= 2;
                }

                return Encoding.Unicode.GetString(buffer, 0, Math.Max(0, length));
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegGetValueW")]
        private static extern int RegGetValue(
            IntPtr hkey,
            string subKey,
            string valueName,
            uint flags,
            out uint type,
            byte[] data,
            ref uint size);
    }
}
