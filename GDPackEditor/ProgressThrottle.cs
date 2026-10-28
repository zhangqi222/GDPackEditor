using System;

namespace GDPackEditor
{
    /// <summary>
    /// 进度刷新节流器：只在「首条 / 末条(100%) / 进度回退 / 百分比增量 ≥1%」时放行。
    /// <para>供 GUI（<c>GuiProgressCallback</c>）与 CLI（<c>CliProgressCallback</c>）两套进度回调**共用**，
    /// 保证两端节流口径完全一致（此前两边各自实现，已出现过口径分叉）。</para>
    /// <para>用途：解包/打包/检测等逐条上报进度的场景，把"每条都刷 UI/终端"降到"每 1% 刷一次"。
    /// 高频重绘的代价很大：CLI 是内核级控制台调用、GUI 是跨线程 BeginInvoke 与控件重绘。</para>
    /// </summary>
    internal sealed class ProgressThrottle
    {
        private int _lastPercent = -1;

        /// <summary>是否应当刷新（返回 true 时会记录本次百分比作为新基准）</summary>
        public bool ShouldReport(int percent)
        {
            int clamped = Math.Max(0, Math.Min(100, percent));
            if (!(clamped >= 100 || _lastPercent < 0
                  || clamped - _lastPercent >= 1 || clamped < _lastPercent))
                return false;
            _lastPercent = clamped;
            return true;
        }

        /// <summary>重置基准（每次开始新操作前调用，等价于"从未上报过"）</summary>
        public void Reset() => _lastPercent = -1;
    }
}
