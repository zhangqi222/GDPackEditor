using System.Buffers;

namespace GDPackEditor
{
    /// <summary>
    /// 大缓冲租借的薄封装（基于 .NET 内建 <see cref="ArrayPool{T}.Shared"/>，零第三方依赖）。
    /// <para>用途：解包/打包里频繁出现的"256KB 级临时缓冲"。直接 <c>new byte[]</c> 会落 LOH
    /// （大对象堆），几万文件 × 每文件/每分块一次的分配量会把 LOH 打满并推高工作集
    /// （ARC 打包实测每文件 1 次 + 每分块 1 次）。统一走这里"租借 → 用完归还"。</para>
    /// <para>约定：① 租到的数组长度**可能大于**请求值，使用方一律以"请求长度/实际读入长度"为准，
    /// 不要依赖 Length；② 归还后内容不可依赖（不复用即不回读）；③ 同一数组只归还一次。</para>
    /// </summary>
    internal static class BufferPool
    {
        /// <summary>租借至少 <paramref name="length"/> 字节的缓冲</summary>
        public static byte[] Rent(int length) => ArrayPool<byte>.Shared.Rent(length);

        /// <summary>归还缓冲（允许 null；请勿重复归还）</summary>
        public static void Return(byte[] buffer)
        {
            if (buffer == null) return;
            try { ArrayPool<byte>.Shared.Return(buffer); } catch { }
        }
    }
}
