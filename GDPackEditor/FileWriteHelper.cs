using System;
using System.IO;

namespace GDPackEditor
{
    /// <summary>
    /// 解包写盘的统一入口（公共化）：建文件 → 写字节 → 恢复包内时间戳。
    /// <para>原实现散落在 ARZ 解包与 ARC 解包两处、写法各自略有差异；统一到这里便于一致维护。</para>
    /// <para>缓冲策略：皆为"一次性写完整块"的用法，故用最小缓冲（bufferSize:1）让写入直达系统调用，
    /// 避免默认 4KB 缓冲的额外拷贝与分块写（小文件尤其明显）。</para>
    /// </summary>
    internal static class FileWriteHelper
    {
        /// <summary>写入文件并设置最后写入时间（<paramref name="fileTime"/> 为默认值时跳过设置）</summary>
        public static void WriteAllBytesWithTime(string path, byte[] data, int length, DateTime fileTime)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1))
                fs.Write(data, 0, length);
            if (fileTime != default)
            {
                try { File.SetLastWriteTime(path, fileTime); } catch { }
            }
        }
    }
}
