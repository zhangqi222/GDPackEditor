using System;
using System.Drawing;

namespace GDPackEditor
{
    /// <summary>
    /// 统一界面缩放管理器（DPI 缩放唯一入口）。
    /// 规则：开关开 = 自动跟随 Windows 系统缩放百分比，硬编码锁定最大 150%；
    ///       开关关 = 按 100% 设计尺寸清晰显示（不做位图拉伸）。
    /// 通过 AntdUI.Config.SetDpi 全局生效，所有 AntdUI.BaseForm 的 AutoHandDpi 自动缩放、
    /// AntdUI 内部渲染（滚动条/图标/SVG）统一走 DpiManager.Scale。
    /// 所有 UI 固定尺寸一律使用 S()/SF() 换算，设计基准为 96 DPI（100%）。
    /// 说明：跨屏拖动时 BaseForm 按系统真实 DPI 触发重缩放，与锁定上限存在边缘差异，后续可精修。
    /// </summary>
    public static class DpiManager
    {
        /// <summary>最大缩放比例（150%），超过系统缩放时锁定，不提供 UI 设置。</summary>
        public const float MaxScale = 1.5f;

        /// <summary>系统当前 DPI 缩放比例（跟随 Windows 系统设定），启动时缓存。</summary>
        private static float _systemScale = 1f;

        /// <summary>是否启用自动缩放（启动时从 SettingsManager.UiScaleEnabled 读取并缓存，重启生效）。</summary>
        private static bool _enabled = true;

        /// <summary>是否启用自动缩放（重启生效）。</summary>
        public static bool Enabled => _enabled;

        /// <summary>系统当前 DPI 缩放比例（跟随 Windows 系统设定）。</summary>
        public static float SystemScale => _systemScale;

        /// <summary>实际生效比例 = Enabled ? Min(SystemScale, MaxScale) : 1f。</summary>
        public static float Scale
        {
            get
            {
                if (!_enabled) return 1f;
                return Math.Min(_systemScale, MaxScale);
            }
        }

        /// <summary>设计像素 → 实际像素（四舍五入，最小为1）。</summary>
        public static int S(int px)
        {
            int v = (int)Math.Round(px * Scale, MidpointRounding.AwayFromZero);
            return v < 1 ? 1 : v;
        }

        /// <summary>设计像素（支持小数，如 10.5 磅）→ 实际像素（四舍五入，最小为1）。</summary>
        public static int S(float px)
        {
            int v = (int)Math.Round(px * Scale, MidpointRounding.AwayFromZero);
            return v < 1 ? 1 : v;
        }

        /// <summary>设计浮点 → 实际浮点。</summary>
        public static float SF(float px) => px * Scale;

        /// <summary>
        /// 程序启动时调用：读取设置，缓存系统 DPI，并应用到 AntdUI 全局。
        /// 必须保证在 SettingsManager 已初始化之后调用。
        /// </summary>
        public static void Apply()
        {
            _systemScale = QuerySystemScale();
            _enabled = SettingsManager.UiScaleEnabled;
            AntdUI.Config.SetDpi(Scale);
        }

        /// <summary>通过临时 GDI 获取系统当前 DPI 比例（不受 Config.SetDpi 自定义值影响）。</summary>
        private static float QuerySystemScale()
        {
            try
            {
                using (var bmp = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(bmp))
                    return AntdUI.Helper.GetDpi(g.DpiX, g.DpiY);
            }
            catch { return 1f; }
        }
    }
}
