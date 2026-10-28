using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using K4os.Compression.LZ4;

namespace GDPackEditor
{
    // Classes for reading and writing ARC files
    //
    // 编码约定（重要）：
    //   · 名字表（包内文件名）→ NameCodec（本机 ACP 写、包级判定读），见 ARCStringTable；
    //   · 下面所有 BinaryReader/BinaryWriter 只做纯二进制字段读写，编码参数无实际影响，
    //     一律显式给 Encoding.UTF8，消除"以后有人 Write(string) 静默写 GBK"的陷阱。

    public class ARCHeader
    {
        public static readonly char[] ARCMAGIC = new char[4] { 'A', 'R', 'C', (char)0x00 };
        public static readonly int HEADER_SIZE = 28;
        public char[] Magic = ARCMAGIC;
        public int Version = 3;
        public int NumberOfFileEntries = 0;
        public int NumberOfDataRecords = 0;
        public long RecordTableSize = 0;  // 修改为long以支持大文件
        public long StringTableSize = 0;  // 修改为long以支持大文件
        public long RecordTableOffset = 0;  // 修改为long以支持大文件

        public void ReadStream(Stream astream)
        {
            using (BinaryReader br = new BinaryReader(astream, Encoding.UTF8, true))
            {
                Magic = br.ReadChars(4);
                Version = br.ReadInt32();
                NumberOfFileEntries = br.ReadInt32();
                NumberOfDataRecords = br.ReadInt32();
                // 官方格式这些字段均为无符号u32（偏移可超过2GB），按uint读取后存入long
                RecordTableSize = br.ReadUInt32();
                StringTableSize = br.ReadUInt32();
                RecordTableOffset = br.ReadUInt32();
            }
        }

        public void WriteStream(Stream astream)
        {
            using (BinaryWriter bw = new BinaryWriter(astream, Encoding.UTF8, true))
            {
                bw.Write(Magic);
                bw.Write(Version);
                bw.Write(NumberOfFileEntries);
                bw.Write(NumberOfDataRecords);
                // 转换为uint写入（低32位），与官方无符号字段一致
                bw.Write((uint)RecordTableSize);
                bw.Write((uint)StringTableSize);
                bw.Write((uint)RecordTableOffset);
            }
        }

        public long GetTocOffset()
        {
            return GetStringTableOffset() + StringTableSize;
        }

        public long GetStringTableOffset()
        {
            return RecordTableOffset + RecordTableSize;
        }

        public bool CheckMagic()
        {
            if (Magic != null)
                return Magic.SequenceEqual(ARCMAGIC);
            return false;
        }

        public void PrintHeader()
        {
            Console.WriteLine("Magic: {0}", CheckMagic() ? "OK" : "Failure");
            Console.WriteLine("Version: {0}", Version);
            Console.WriteLine("Number Of File Entries: {0}", NumberOfFileEntries);
            Console.WriteLine("Number Of Data Records: {0}", NumberOfDataRecords);
            Console.WriteLine("Record Table Size: {0}", RecordTableSize);
            Console.WriteLine("String Table Size: {0}", StringTableSize);
            Console.WriteLine("Record Table Offset: 0x{0:X4}", RecordTableOffset);
        }

    }

    public class ARCStringTable {
        MemoryStream smem = null;

        /// <summary>
        /// 名字表解码所用编码。默认 = 本机 ANSI 码页（NameCodec.Acp），
        /// 由 ARCFile.ReadStream 读完 TOC 后按包级判定（ArcNameResolver）统一设定。
        /// </summary>
        public Encoding NameEncoding = NameCodec.Acp;

        /// <summary>包级判定结果（含判定编码与逐名兜底）；未做判定时为 null</summary>
        public ArcNameResolveResult NameResolve = null;

        public ARCStringTable()
        {

        }

        public void ReadFromStream(Stream astream, int tablesize) 
        {
            smem = new MemoryStream(tablesize);
            byte[] buff = new byte[tablesize];
            astream.Read(buff, 0, tablesize);
            smem.Write(buff, 0, tablesize);
        }

        public int WriteToStream(Stream tstream)
        {
            if (smem == null || smem.Length == 0) return 0;
            tstream.Write(smem.ToArray(), 0, (int)smem.Length);
            return (int)smem.Length;
        }

        /// <summary>读取名字原始字节（读侧编码判定的唯一前置：判定必须拿到字节）</summary>
        public byte[] GetBytesAt(int offset, int len)
        {
            if (smem == null || len <= 0) return new byte[0];
            byte[] buff = smem.GetBuffer();
            if (offset < 0 || offset + len > smem.Length) return new byte[0];
            byte[] result = new byte[len];
            Array.Copy(buff, offset, result, 0, len);
            return result;
        }

        /// <summary>
        /// 就地判断该名字是否纯 ASCII —— **不分配任何缓冲**。
        /// 供"全 ASCII 快速短路"使用（现实 100% 的包走这条路，必须零开销）。
        /// </summary>
        public bool IsAsciiAt(int offset, int len)
        {
            if (smem == null || len <= 0) return true;
            byte[] buff = smem.GetBuffer();
            if (offset < 0 || offset + len > smem.Length) return true;
            return Ascii.IsValid(buff.AsSpan(offset, len));
        }

        public string GetStringAt(int offset, int len)
        {
            if (smem == null)
            {
                Program.Log.Warn("ARCStringTable => GetStringAt: Trying to read uninitialized stringtable!");
                return "";
            }

            if (len <= 0) return "";

            byte[] raw = GetBytesAt(offset, len);
            if (raw.Length == 0) return "";

            // 已做包级判定 → 按判定编码解，个别失败者逐名兜底
            if (NameResolve != null)
                return ArcNameResolver.Decode(raw, NameResolve, out _);

            return NameEncoding.GetString(raw);
        }


        /// <summary>
        /// 追加一个名字到名字表尾部，返回该名字在表中的字节偏移。
        /// 按本机 ACP 严格编码；本机码页表示不了的字符抛出异常（写侧预检应已提前拦截并跳过该文件）。
        /// </summary>
        /// <param name="byteLength">实际写入的名字字节数（不含 NUL 终止符），可直接用作 StringEntryLength</param>
        public int Append(string astring, out int byteLength)
        {
            if (smem == null)
                smem = new MemoryStream();
            if (astring == null) astring = string.Empty;

            byte[] nameBytes;
            char badChar;
            int badCodePoint;
            if (!NameCodec.TryEncode(astring, out nameBytes, out badChar, out badCodePoint))
            {
                throw new EncoderFallbackException(string.Format(
                    LanguageManager.Instance.GetText("ArcName.CannotEncode",
                        "文件名无法用本机码页 {0} 表示：\"{1}\"（字符 '{2}' U+{3:X4}）"),
                    NameCodec.AcpName, astring, badChar, badCodePoint));
            }

            smem.Seek(0, SeekOrigin.End);
            int pos = (int)smem.Position;
            smem.Write(nameBytes, 0, nameBytes.Length);
            // 非空名字必须带 NUL 终止符（空名条目的 NUL 属可选项）
            smem.WriteByte(0);
            byteLength = nameBytes.Length;
            return pos;
        }
    }

    public class ARCTocEntry
    {
        public int EntryType;
        public long FileOffset;  // 修改为long以支持大文件
        public long CompressedSize;  // 修改为long以支持大文件
        public long DecompressedSize;  // 修改为long以支持大文件
        public int DecompressedHash;
        public DateTime FileTime; // 8 Bytes/long
        public int FileParts;
        public int FirstPartIndex;
        public int StringEntryLength;
        public int StringEntryOffset;

        public void ReadStream(Stream astream)
        {
            using (BinaryReader br = new BinaryReader(astream, Encoding.UTF8, true)) {
                EntryType = br.ReadInt32();
                // 读取为uint（无符号）后存储为long，偏移可超过2GB
                FileOffset = br.ReadUInt32();
                CompressedSize = br.ReadUInt32();
                DecompressedSize = br.ReadUInt32();
                DecompressedHash = br.ReadInt32();
                FileTime = DateTime.FromFileTimeUtc(br.ReadInt64());
                FileParts = br.ReadInt32();
                FirstPartIndex = br.ReadInt32();
                StringEntryLength = br.ReadInt32();
                StringEntryOffset = br.ReadInt32();
            }
        }

        public void WriteStream(Stream astream)
        {
            using (BinaryWriter bw = new BinaryWriter(astream, Encoding.UTF8, true))
            {
                // EntryType = br.ReadInt32();
                bw.Write(EntryType);
                // FileOffset = br.ReadInt32();
                bw.Write((uint)FileOffset);
                // CompressedSize = br.ReadInt32();
                bw.Write((uint)CompressedSize);
                // DecompressedSize = br.ReadInt32();
                bw.Write((uint)DecompressedSize);
                // DecompressedHash = br.ReadInt32();
                bw.Write(DecompressedHash);
                // FileTime = DateTime.FromFileTimeUtc(br.ReadInt64());
                bw.Write(FileTime.ToFileTimeUtc());
                // FileParts = br.ReadInt32();
                bw.Write(FileParts);
                // FirstPartIndex = br.ReadInt32();
                bw.Write(FirstPartIndex);
                // StringEntryLength = br.ReadInt32();
                bw.Write(StringEntryLength);
                // StringEntryOffset = br.ReadInt32();
                bw.Write(StringEntryOffset);
            }
        }

        public void PrintTocEntry(ARCStringTable strtable = null) {
            Console.WriteLine("Entry Type: {0}", EntryType);
            Console.WriteLine("File Offset: 0x{0:X4}", FileOffset);
            Console.WriteLine("Compressed Size: {0}", CompressedSize);
            Console.WriteLine("Decompressed Size: {0}", DecompressedSize);
            Console.WriteLine("Decompressed Hash: 0x{0:X4}", DecompressedHash);
            Console.WriteLine("File Time: {0}", FileTime);
            Console.WriteLine("File Parts: {0}", FileParts);
            Console.WriteLine("First Part Index: {0}", FirstPartIndex);
            Console.WriteLine("String Entry Length: {0}", StringEntryLength);
            Console.WriteLine("String Entry Offset: 0x{0:X4}", StringEntryOffset);
            if (strtable != null) {
                Console.WriteLine("String Entry: \"{0}\"", GetEntryString(strtable));
            }
        }

        public string GetEntryString(ARCStringTable strtable = null)
        {
            return strtable != null? strtable.GetStringAt(StringEntryOffset, StringEntryLength): "";
        }
    }

    public class ARCFilePart
    {
        public long PartOffset;  // 修改为long以支持大文件
        public long CompressedSize;  // 修改为long以支持大文件
        public long DecompressedSize;  // 修改为long以支持大文件

        public void ReadStream(Stream astream)
        {
            using (BinaryReader br = new BinaryReader(astream, Encoding.UTF8, true))
            {
                // 读取为uint（无符号）后存储为long，偏移可超过2GB
                PartOffset = br.ReadUInt32();
                CompressedSize = br.ReadUInt32();
                DecompressedSize = br.ReadUInt32();
            }
        }

        public void PrintFilePart()
        {
            Console.WriteLine("Part Offset: 0x{0:X4}", PartOffset);
            Console.WriteLine("Compressed Size: {0}", CompressedSize);
            Console.WriteLine("Decompressed Size: {0}", DecompressedSize);
        }
    }

    public class ARCWriter : IDisposable
    {
        public static readonly int MAX_BLOCK_SIZE = 256 * 1024;
        ARCHeader whdr = null;
        ARCStringTable wstrs = null;
        List<ARCTocEntry> wtoc = null;
        List<ARCFilePart> wparts = null;
        Stream wstream = null;
        bool started = false;
        bool finished = false;

        public ARCWriter(Stream outstream)
        {
            wstream = outstream;

            wstrs = new ARCStringTable();
            wtoc = new List<ARCTocEntry>();
            wparts = new List<ARCFilePart>();
            BeginWrite();
        }

        public long WriteRecordTable(List<ARCFilePart> aparts, Stream astream)
        {
            using (BinaryWriter bw = new BinaryWriter(astream, Encoding.UTF8, true))
                for (int p = 0; p < aparts.Count; p++)
                {
                    // 转换为uint写入（低32位），与官方无符号字段一致
                    bw.Write((uint)aparts[p].PartOffset);
                    bw.Write((uint)aparts[p].CompressedSize);
                    bw.Write((uint)aparts[p].DecompressedSize);
                }
            return (long)aparts.Count * 12;
        }

        public void WriteFromTocEntry(ARCFile afile, ARCTocEntry aentry)
        {
            string entryname = aentry.GetEntryString(afile.strs);
            ARCTocEntry newentry = new ARCTocEntry();
            int nameByteLength;
            newentry.StringEntryOffset = wstrs.Append(entryname, out nameByteLength);
            // 名字字节数与写入同源（Append 的返回值），杜绝"长度与字节脱节"
            newentry.StringEntryLength = nameByteLength;
            newentry.FileOffset = wstream.Position;
            newentry.FileParts = aentry.FileParts;
            newentry.FirstPartIndex = wparts.Count;
            // Write compressed parts:
            // 缓冲租借一次循环复用：原实现每个 part 都 new 一块（压缩后大小可达 256KB 级，落 LOH）
            byte[] partsBuf = BufferPool.Rent(LZ4Codec.MaximumOutputSize(MAX_BLOCK_SIZE));
            try
            {
                for (int p = 0; p < aentry.FileParts; p++)
                {
                    ARCFilePart newpart = new ARCFilePart();
                    ARCFilePart cpart = afile.parts[aentry.FirstPartIndex + p];
                    newpart.CompressedSize = cpart.CompressedSize;
                    newpart.DecompressedSize = cpart.DecompressedSize;
                    newpart.PartOffset = wstream.Position; // Remember offset
                    // Get compressed part
                    int csize = (int)Math.Min(cpart.CompressedSize, int.MaxValue);
                    byte[] cbuff = csize <= partsBuf.Length ? partsBuf : BufferPool.Rent(csize);   // 异常大 part 兜底
                    try
                    {
                        afile.fstream.Seek(cpart.PartOffset, SeekOrigin.Begin);
                        afile.fstream.Read(cbuff, 0, csize);
                        // Write actual part:
                        wstream.Write(cbuff, 0, csize);
                    }
                    finally
                    {
                        if (!ReferenceEquals(cbuff, partsBuf)) BufferPool.Return(cbuff);
                    }
                    wparts.Add(newpart); // Addit to written part list
                }
            }
            finally
            {
                BufferPool.Return(partsBuf);
            }
            // Now we'll trust original entry has all correct data
            newentry.EntryType = aentry.EntryType;
            newentry.CompressedSize = aentry.CompressedSize;
            newentry.DecompressedSize = aentry.DecompressedSize;
            newentry.DecompressedHash = aentry.DecompressedHash;
            newentry.FileTime = aentry.FileTime;
            wtoc.Add(newentry); // Add it to write TOC
        }

        /// <summary>
        /// 将旧ARC条目以游戏原生空条目格式写入墓碑（EntryType=0、空名、无数据块）。
        /// 数据不复制，由固实（CompactArc）回收；游戏引擎将其当作删除标记跳过。
        /// </summary>
        public void WriteTombstoneEntry(ARCFile afile, ARCTocEntry aentry)
        {
            // 原生墓碑：模仿游戏原生的空条目（EntryType=0、空名、无数据块）。
            // 数据不复制、留在原文件内，由固实（CompactArc）清理回收；
            // 游戏引擎把 EntryType=0 空条目当作删除标记处理，不会作为有效资源加载。
            ARCTocEntry newentry = new ARCTocEntry();
            newentry.EntryType = 0;
            int nameByteLength;
            newentry.StringEntryOffset = wstrs.Append("", out nameByteLength);
            newentry.StringEntryLength = 0;
            newentry.FileOffset = aentry.FileOffset;
            newentry.FileParts = 0;
            newentry.FirstPartIndex = 0;
            newentry.CompressedSize = 0;
            newentry.DecompressedSize = 0;
            newentry.DecompressedHash = 0;
            newentry.FileTime = aentry.FileTime;
            wtoc.Add(newentry);
        }

        public void WriteFromStream(string entryname, DateTime entrytime, Stream astream)
        {
            ARCTocEntry newentry = new ARCTocEntry();
            int nameByteLength;
            newentry.StringEntryOffset = wstrs.Append(entryname, out nameByteLength);
            // 名字字节数与写入同源：用字符数会丢中文文件名的扩展名，按 GBK 单独算又会与写入脱节
            newentry.StringEntryLength = nameByteLength;
            newentry.FileOffset = wstream.Position;
            // newentry.FileParts = (int)astream.Length / MAX_BLOCK_SIZE;
            newentry.FirstPartIndex = wparts.Count;
            int read = 0, partcount = 0;
            long csize = 0;
            // 缓冲租借一次循环复用：原实现每文件 new 一块 256KB 输入缓冲 + **每个分块**再 new 一块
            // 压缩输出缓冲（几万文件时全是 LOH 分配）。压缩输出缓冲提到循环外，大小覆盖任一分块。
            byte[] buffer = BufferPool.Rent(MAX_BLOCK_SIZE);
            byte[] cbuffer = BufferPool.Rent(LZ4Codec.MaximumOutputSize(MAX_BLOCK_SIZE));
            // Adler32;
            Adler32 adler = new Adler32();
            try
            {
            while ((read = astream.Read(buffer, 0, MAX_BLOCK_SIZE)) > 0) {
                // newblock
                adler.ComputeHash(buffer, 0, read);
                ARCFilePart newpart = new ARCFilePart();
                newpart.PartOffset = wstream.Position;
                newpart.DecompressedSize = (long)read;
                
                // 使用标准老式LZ4Codec.Encode方法压缩数据
                // 使用LZ4Codec.Encode进行压缩
                int compressedLength = LZ4Codec.Encode(buffer, 0, read, cbuffer, 0, cbuffer.Length);
                
                if (compressedLength > 0 && compressedLength < read)
                {
                    // 如果压缩有效（有大小减少）
                    newpart.CompressedSize = (long)compressedLength;
                    wstream.Write(cbuffer, 0, compressedLength);
                }
                else
                {
                    // 如果压缩无效或失败，则使用原始数据
                    newpart.CompressedSize = (long)read;
                    wstream.Write(buffer, 0, read);
                }
                
                wparts.Add(newpart);
                csize += newpart.CompressedSize;
                partcount += 1;
            }
            }
            finally
            {
                BufferPool.Return(buffer);
                BufferPool.Return(cbuffer);
            }
            newentry.FileParts = partcount <= int.MaxValue ? (int)partcount : int.MaxValue;
            newentry.EntryType = 3; // TODO: What are other possible types
            newentry.CompressedSize = (long)csize;
            newentry.DecompressedSize = (long)astream.Length;
            newentry.DecompressedHash = (int)adler.checksum;
            newentry.FileTime = entrytime;
            wtoc.Add(newentry);
        }

        public void BeginWrite()
        {
            // Write header
            if (!started)
            {
                whdr = new ARCHeader();
                // whdr.WriteStream(wstream); // Empty header for now
                byte[] zeroes = new byte[0x800];
                Array.Clear(zeroes, 0, 0x800);
                wstream.Write(zeroes, 0, 0x800);
                started = true;
            }
        }

        public void FinishWrite()
        {
            whdr.RecordTableOffset = wstream.Position;
            whdr.RecordTableSize = WriteRecordTable(wparts, wstream); // Write part table
            whdr.NumberOfDataRecords = wparts.Count;
            whdr.StringTableSize = wstrs.WriteToStream(wstream);
            // Now write TOC table
            whdr.NumberOfFileEntries = wtoc.Count;
            for (int i = 0; i < wtoc.Count; i++)
            {
                wtoc[i].WriteStream(wstream);
            }
            // Rewrite finished header:
            wstream.Seek(0, SeekOrigin.Begin);
            whdr.WriteStream(wstream);
            wstream.Seek(0, SeekOrigin.End); // Move cursor to end
            finished = true;
        }

        public void Dispose() {
            if (started && !finished)
                FinishWrite();
        }

        ~ARCWriter() {
            Dispose();
        }

    }

    public class ARCFile
    {
        public ARCHeader hdr = null;
        public ARCStringTable strs = null;
        public List<ARCTocEntry> toc = null;
        public List<ARCFilePart> parts = null;
        public Stream fstream = null;

        /// <summary>
        /// 读侧名字编码判定结论的输出委托（GUI 传 progress.ReportLog，编辑器传自身日志方法，CLI 传 CliOut）。
        /// 为 null = 静默 —— 用于"为比对内容而反复重读同一个包"的场景，避免同一结论刷屏。
        /// 只在包内出现非 ASCII 名时才可能输出。
        /// </summary>
        public Action<string> NameReporter = null;

        /// <summary>
        /// 结论行前缀（如"基准包 / 并入包"）。同一操作里读多个包时用它区分结论归属；为空则不加。
        /// </summary>
        public string NameReporterPrefix = null;

        public ARCFile()
        {
        }

        public void ReadStream(Stream astream)
        {
            fstream = astream;
            hdr = new ARCHeader();
            hdr.ReadStream(astream);
            // hdr.PrintHeader(); // DEBUG:

            // 布局校验：tocOffset + 三表大小必须落在文件范围内，防止损坏文件定位到错误位置
            long metaEnd = hdr.RecordTableOffset + hdr.RecordTableSize + hdr.StringTableSize;
            long tocEnd = metaEnd + (long)hdr.NumberOfFileEntries * 44;
            if (hdr.RecordTableOffset < 0 || hdr.RecordTableOffset > astream.Length
                || metaEnd < hdr.RecordTableOffset || tocEnd > astream.Length)
            {
                throw new InvalidDataException(LanguageManager.Instance.GetText("ArcFile.BadStructure",
                    "ARC 文件结构异常：数据表偏移超出文件范围，文件可能已损坏或不是有效的 ARC 文件。"));
            }

            // Read part table:
            // TODO - take out creating and disposing of BinaryReader out of the loop
            parts = new List<ARCFilePart>();
            astream.Seek(hdr.RecordTableOffset, SeekOrigin.Begin);
            for (int i = 0; i < hdr.NumberOfDataRecords; i++)
            {
                ARCFilePart prec = new ARCFilePart();
                prec.ReadStream(astream);
                parts.Add(prec);
            }

            // Now read stringtable
            strs = new ARCStringTable();
            astream.Seek(hdr.GetStringTableOffset(), SeekOrigin.Begin);
            strs.ReadFromStream(astream, (int)Math.Min(hdr.StringTableSize, int.MaxValue));           

            // Now read TOC:
            toc = new List<ARCTocEntry>();
            toc.Capacity = (int)Math.Min(hdr.NumberOfFileEntries, int.MaxValue);
            astream.Seek(hdr.GetTocOffset(), SeekOrigin.Begin);
            for (int i = 0; i < hdr.NumberOfFileEntries; i++)
            {
                ARCTocEntry tocentry = new ARCTocEntry();
                tocentry.ReadStream(astream);
                /*// DEBUG:
                if (tocentry.GetEntryString(strs) == "")
                    tocentry.PrintTocEntry(strs); // Debug
                if (tocentry.EntryType != 3)
                    tocentry.PrintTocEntry(strs);
                */
                toc.Add(tocentry);
            }

            // 读侧名字编码：全 ASCII → 零开销短路（现实 100%）；含非 ASCII → 包级判定一次，整包复用
            ResolveNames();
        }

        /// <summary>
        /// 读侧包级判定（规范见 skill §5）：定出整包"包内编码"并写入名字表，
        /// 之后所有名字解码（列表/解包/比对/合并）都统一跟随该编码。
        /// 全 ASCII 包直接返回 —— 不做判定、不输出任何编码相关日志。
        /// </summary>
        private void ResolveNames()
        {
            if (strs == null || toc == null) return;

            // 全 ASCII 短路（现实 100% 的包走这条路）：就地判断，**不分配任何缓冲**
            bool anyNonAscii = false;
            foreach (ARCTocEntry entry in toc)
            {
                if (entry.StringEntryLength <= 0) continue;
                if (!strs.IsAsciiAt(entry.StringEntryOffset, entry.StringEntryLength))
                {
                    anyNonAscii = true;
                    break;
                }
            }
            if (!anyNonAscii) return;

            // 判定必须拿到原始字节：按 nameOffset + nameLength 定位，跳过死条目与空名
            var buffers = new List<byte[]>();
            foreach (ARCTocEntry entry in toc)
            {
                if (entry.StringEntryLength <= 0) continue;
                byte[] raw = strs.GetBytesAt(entry.StringEntryOffset, entry.StringEntryLength);
                if (raw.Length == 0) continue;
                buffers.Add(raw);
            }
            if (buffers.Count == 0) return;

            ArcNameResolveResult result = ArcNameResolver.Resolve(buffers);
            strs.NameResolve = result;
            strs.NameEncoding = result.Chosen;

            // 读侧编码结论：只给用户两个值 —— 包内记录名编码、本机编码环境。
            // 判定依据（L1~L5 + 分桶计数）属排查用细节，移到"日志显示详情"开关后面，默认不显示。
            ReportName(string.Format(
                LanguageManager.Instance.GetText("ArcName.ResolveInfo", "包内记录名编码：{0}，本机编码环境：{1}"),
                result.ChosenName, NameCodec.AcpName));

            if (!SettingsManager.ShowDetailLog) return;

            // 详情级：判定依据（排查"编码被判错"时打开"日志显示详情"即可看到）
            ReportName(string.Format(
                LanguageManager.Instance.GetText("ArcName.ResolveEvidence", "包内编码判定依据：{0}"),
                result.Evidence));

            // 详情级：歧义名的另一种解读
            foreach (string sample in result.AmbiguousSamples)
            {
                ReportName(string.Format(
                    LanguageManager.Instance.GetText("ArcName.AmbiguousItem", "歧义名 {0}（已按 {1}）"),
                    sample, result.ChosenName));
            }

            // 详情级：个别解不了的名字走了哪种兜底（限流，避免刷屏）
            int fallbackShown = 0;
            foreach (byte[] b in buffers)
            {
                if (fallbackShown >= 20) break;
                string decoded = ArcNameResolver.Decode(b, result, out string fellBackTo);
                if (fellBackTo == null) continue;
                ReportName(string.Format(
                    LanguageManager.Instance.GetText("ArcName.FallbackItem", "名字按 {0} 回退解读：{1}"),
                    fellBackTo, decoded));
                fallbackShown++;
            }
        }

        /// <summary>输出一条读侧编码结论（未设置输出通道时静默，不影响解码结果）</summary>
        private void ReportName(string text)
        {
            if (NameReporter == null) return;
            if (!string.IsNullOrEmpty(NameReporterPrefix))
                text = NameReporterPrefix + " " + text;
            NameReporter(text);
        }

        public void UnpackToStream(ARCTocEntry aentry, Stream tostream)
        {
            if (aentry.EntryType == 1 && aentry.CompressedSize == aentry.DecompressedSize)
            {
                fstream.Seek(aentry.FileOffset, SeekOrigin.Begin);
                CopyBytes(aentry.CompressedSize, fstream, tostream);
            } 
            else
            {
                for (int p = 0; p < aentry.FileParts; p++)
                {
                    ARCFilePart cpart = parts[aentry.FirstPartIndex + p];
                    // 使用long确保能够处理大文件部分
                    byte[] cbuff = new byte[(int)Math.Min(cpart.CompressedSize, int.MaxValue)];
                    fstream.Seek(cpart.PartOffset, SeekOrigin.Begin);
                    int bytesRead = fstream.Read(cbuff, 0, cbuff.Length);
                    
                    // 确保读取到了预期的字节数
                    if (bytesRead < cbuff.Length)
                    {
                        Console.WriteLine(string.Format(
                            LanguageManager.Instance.GetText("Cli.PartialReadWarning",
                                "警告: 读取文件部分时只读取到{0}字节，预期{1}字节"),
                            bytesRead, cbuff.Length));
                    }
                    
                    if (cpart.CompressedSize < cpart.DecompressedSize)
                    {
                        // 使用LZ4Codec进行解压
                        byte[] dbuff = new byte[(int)Math.Min(cpart.DecompressedSize, int.MaxValue)];
                        try
                        {
                            // 使用实际读取的字节数进行解压
                            int decompressedSize = LZ4Codec.Decode(cbuff, 0, bytesRead, dbuff, 0, (int)Math.Min(cpart.DecompressedSize, int.MaxValue));
                            tostream.Write(dbuff, 0, decompressedSize);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("LZ4 decompression error: " + ex.Message);
                            // 修复错误处理：解压失败时只写入实际读取的字节数，而不是期望的解压大小
                            tostream.Write(cbuff, 0, bytesRead);
                        }
                    } 
                    else
                    {
                        // 写入实际读取的字节数
                        tostream.Write(cbuff, 0, bytesRead);
                    }
                }
            }
        }

        /// <summary>
        /// 并行只读扫描用：使用外部数据流解压，不触碰实例流（线程安全）。
        /// 调用前请确保 toc/parts 元数据已加载完毕。
        /// </summary>
        public void UnpackToStream(ARCTocEntry aentry, Stream src, Stream tostream)
        {
            if (aentry.EntryType == 1 && aentry.CompressedSize == aentry.DecompressedSize)
            {
                src.Seek(aentry.FileOffset, SeekOrigin.Begin);
                CopyBytes(aentry.CompressedSize, src, tostream);
            }
            else
            {
                for (int p = 0; p < aentry.FileParts; p++)
                {
                    ARCFilePart cpart = parts[aentry.FirstPartIndex + p];
                    byte[] cbuff = new byte[(int)Math.Min(cpart.CompressedSize, int.MaxValue)];
                    src.Seek(cpart.PartOffset, SeekOrigin.Begin);
                    int bytesRead = src.Read(cbuff, 0, cbuff.Length);

                    if (bytesRead < cbuff.Length)
                    {
                        Console.WriteLine(string.Format(
                            LanguageManager.Instance.GetText("Cli.PartialReadWarning",
                                "警告: 读取文件部分时只读取到{0}字节，预期{1}字节"),
                            bytesRead, cbuff.Length));
                    }

                    if (cpart.CompressedSize < cpart.DecompressedSize)
                    {
                        byte[] dbuff = new byte[(int)Math.Min(cpart.DecompressedSize, int.MaxValue)];
                        try
                        {
                            int decompressedSize = LZ4Codec.Decode(cbuff, 0, bytesRead, dbuff, 0, (int)Math.Min(cpart.DecompressedSize, int.MaxValue));
                            tostream.Write(dbuff, 0, decompressedSize);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("LZ4 decompression error: " + ex.Message);
                            tostream.Write(cbuff, 0, bytesRead);
                        }
                    }
                    else
                    {
                        tostream.Write(cbuff, 0, bytesRead);
                    }
                }
            }
        }

        public void RepackToStream(Stream tstream)
        {
            // BeginWrite(tstream);
            using (ARCWriter awriter = new ARCWriter(tstream))
            {
                foreach (ARCTocEntry entry in toc)
                {
                    awriter.WriteFromTocEntry(this, entry);
                }
            }
        }

        public void RepackToFile(string filename) {
            using (FileStream fs = new FileStream(filename, FileMode.Create))
            {
                RepackToStream(fs);
            }
        }


        public void UnpackToFile(ARCTocEntry aentry, string filename)
        {
            try
            {
                using (FileStream fs = new FileStream(filename, FileMode.Create))
                {
                    UnpackToStream(aentry, fs);
                }
            }
            catch (Exception ex)
            {
                // 记录错误但不中断整个解包过程
                Console.WriteLine(string.Format(
                    LanguageManager.Instance.GetText("Cli.UnpackFileFailed", "无法解包文件 '{0}': {1}"),
                    filename, ex.Message));
                // 如果需要更详细的日志，可以添加到程序的日志系统中
                // Program.Log.Warn("无法解包文件 '{0}': {1}", filename, ex.Message);
            }
        }

        public void UnpackAll(string tofolder)
        {
            foreach (ARCTocEntry aentry in toc)
            {
                string entrystr = aentry.GetEntryString(strs);
                if (aentry.EntryType != 3)
                    aentry.PrintTocEntry();
                if (string.IsNullOrEmpty(entrystr)) continue; // TODO: Important - What are these empty entries? they have some packed data, but have no blocks and type is 0
                string afilename = Path.Combine(tofolder, entrystr.Replace('/', Path.DirectorySeparatorChar));
                
                try
                {
                    // 确保目标目录存在
                    string directoryName = Path.GetDirectoryName(afilename);
                    if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
                    {
                        Directory.CreateDirectory(directoryName);
                    }
                    
                    // 解包文件
                    UnpackToFile(aentry, afilename);
                }
                catch (Exception ex)
                {
                    // 记录错误但不中断整个解包过程
                    Console.WriteLine(string.Format(
                        LanguageManager.Instance.GetText("Cli.ProcessFileFailed", "处理文件 '{0}' 时出错: {1}"),
                        afilename, ex.Message));
                }
            }
        }

        /// <summary>
        /// 获取ARC文件中的所有条目名称列表
        /// </summary>
        /// <returns>文件路径列表</returns>
        public List<string> ListAllEntries()
        {
            List<string> entries = new List<string>();
            if (toc == null || strs == null)
            {
                Program.Log.Warn(LanguageManager.Instance.GetText("ArcFile.NotInitialized",
                    "ARCFile未初始化，无法获取文件列表"));
                return entries;
            }

            foreach (ARCTocEntry aentry in toc)
            {
                string entrystr = aentry.GetEntryString(strs);
                if (string.IsNullOrEmpty(entrystr))
                {
                    entries.Add("(null)"); // 显示空文件名为null
                }
                else
                {
                    entries.Add(entrystr);
                }
            }
            return entries;
        }

        /// <summary>
        /// 获取条目名→DecompressedHash的映射（用于增量变化检测）
        /// </summary>
        public Dictionary<string, int> GetEntryHashMap()
        {
            var map = new Dictionary<string, int>();
            if (toc == null || strs == null) return map;
            for (int i = 0; i < toc.Count; i++)
            {
                var entry = toc[i];
                string name = entry.GetEntryString(strs);
                if (!string.IsNullOrEmpty(name))
                {
                    if (!map.ContainsKey(name))
                        map.Add(name, entry.DecompressedHash);
                }
            }
            return map;
        }

        /// <summary>
        /// 按名称获取TOC条目索引（用于选择性复制）
        /// </summary>
        public int GetEntryIndex(string name)
        {
            if (toc == null || strs == null) return -1;
            for (int i = 0; i < toc.Count; i++)
            {
                if (string.Equals(toc[i].GetEntryString(strs), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// 获取条目名→TOC索引的映射（不区分大小写，用于合并时避免循环内线性扫描）
        /// </summary>
        public Dictionary<string, int> GetEntryIndexMap()
        {
            var map = new Dictionary<string, int>(toc?.Count ?? 0, StringComparer.OrdinalIgnoreCase);
            if (toc == null || strs == null) return map;
            for (int i = 0; i < toc.Count; i++)
            {
                var name = toc[i].GetEntryString(strs);
                if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
                    map.Add(name, i);
            }
            return map;
        }

        /// <summary>
        /// 固实：跳过无效条目，只保留有效数据
        /// </summary>
        public void CompactToStream(Stream output)
        {
            using (ARCWriter awriter = new ARCWriter(output))
            {
                for (int i = 0; i < toc.Count; i++)
                {
                    var entry = toc[i];
                    string name = entry.GetEntryString(strs);
                    if (string.IsNullOrEmpty(name) || entry.EntryType != 3)
                        continue;
                    awriter.WriteFromTocEntry(this, entry);
                }
            }
        }

        /// <summary>
        /// 打印ARC文件中的所有条目名称
        /// </summary>
        public void PrintAllEntries()
        {
            List<string> entries = ListAllEntries();
            Program.Log.Info(string.Format(
                LanguageManager.Instance.GetText("ArcFile.EntryCount", "ARC文件包含 {0} 个条目:"), entries.Count));
            foreach (string entry in entries)
            {
                Program.Log.Info(entry);
            }
        }


        public static long CopyBytes(long bytesRequired, Stream inStream, Stream outStream)
        {
            long readSoFar = 0L;
            // 增加缓冲区大小以提高大文件处理效率
            var buffer = new byte[1024 * 1024]; // 1MB缓冲区
            do
            {
                var toRead = Math.Min(bytesRequired - readSoFar, buffer.Length);
                var readNow = inStream.Read(buffer, 0, (int)toRead);
                if (readNow == 0)
                    break; // End of stream
                outStream.Write(buffer, 0, readNow);
                readSoFar += readNow;
            } while (readSoFar < bytesRequired);
            return readSoFar;
        }
    }
}
