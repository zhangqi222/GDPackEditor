using System;

namespace GDPackEditor
{
    /// <summary>
    /// 移除 NLog 后，保留 <c>Program.Log</c> 原有调用形状的转发器：全部转发到 <see cref="LogService"/>，
    /// 从而无需改动既有 47 处调用点。支持 NLog 风格的 {0} 占位符格式化（等价 string.Format）。
    /// </summary>
    public sealed class ArzLogger
    {
        public void Debug(string message) => LogService.Debug(message);
        public void Debug(string format, params object[] args) => LogService.Debug(Format(format, args));

        public void Info(string message) => LogService.Info(message);
        public void Info(string format, params object[] args) => LogService.Info(Format(format, args));

        public void Warn(string message) => LogService.Warn(message);
        public void Warn(string format, params object[] args) => LogService.Warn(Format(format, args));

        public void Error(string message) => LogService.Error(message);
        public void Error(string format, params object[] args) => LogService.Error(Format(format, args));

        public void Fatal(string message) => LogService.Fatal(message);
        public void Fatal(string format, params object[] args) => LogService.Fatal(Format(format, args));

        /// <summary>格式化占位符；格式串非法时回退为原文拼接，避免"记日志本身抛异常"</summary>
        private static string Format(string format, object[] args)
        {
            if (format == null) return "";
            if (args == null || args.Length == 0) return format;
            try { return string.Format(format, args); }
            catch (FormatException) { return format + " " + string.Join(" ", args); }
        }
    }
}
