using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using K4os.Compression.LZ4;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;


namespace GDPackEditor {
    // Classes responsible for Reading/Writing arz database, and database objects themselves

    /// <summary>
    /// ARZ文件操作主类，封装读取和条目列表功能
    /// </summary>
    public class ARZFile
    {
        private ARZReader reader;
        public ARZReader Reader { get { return reader; } }
        
        /// <summary>
        /// 从流中读取ARZ文件内容
        /// </summary>
        /// <param name="stream">ARZ文件流</param>
        public void ReadStream(Stream stream)
        {
            reader = new ARZReader(stream);
        }
        
        /// <summary>
        /// 获取ARZ文件中所有条目名称列表
        /// </summary>
        /// <returns>条目名称列表</returns>
        public List<string> ListAllEntries()
        {
            if (reader == null)
                return new List<string>();
                
            return reader.GetAllRecordNames();
        }
        
        /// <summary>
        /// 分批获取ARZ文件中所有条目名称
        /// </summary>
        /// <param name="batchSize">每批处理的记录数量</param>
        /// <returns>条目名称的迭代器</returns>
        public IEnumerable<string> ListAllEntriesInBatches(int batchSize = 1000)
        {
            if (reader == null)
                yield break;
                
            foreach (string recordName in reader.GetRecordNamesInBatches(batchSize))
            {
                yield return recordName;
            }
        }
        
        /// <summary>
        /// 通过索引范围获取ARZ文件中条目名称
        /// </summary>
        /// <param name="startIndex">起始索引</param>
        /// <param name="count">要获取的记录数量</param>
        /// <returns>指定范围的条目名称列表</returns>
        public List<string> ListEntriesByRange(int startIndex, int count)
        {
            if (reader == null)
                return new List<string>();
                
            return reader.GetRecordNamesByRange(startIndex, count);
        }
        
        /// <summary>
        /// 处理ARZ文件中的所有记录（移除了分批和并行限制）
        /// </summary>
        /// <param name="processor">处理单个记录的函数</param>
        public void ProcessRecordsInParallel(Action<string, ARZRecord> processor)
        {
            if (reader == null || processor == null)
                return;
            
            // 确保记录表已加载
            reader.EnsureRecordTableIsLoaded();
            int totalRecords = reader.Count;
            
            // 顺序处理所有记录，不使用分批和并行处理
            for (int i = 0; i < totalRecords; i++)
            {
                try
                {
                    // 获取记录名称
                    string recordName = reader.GetRecordName(i);
                    
                    // 按需加载记录数据
                    ARZRecord record = reader.GetRecord(i);
                    
                    // 处理记录
                    processor(recordName, record);
                    
                    // 使用完后释放记录数据，节省内存
                    record.DiscardData();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(string.Format(
                        LanguageManager.Instance.GetText("Cli.ProcessRecordFailed", "处理记录时出错: {0}"),
                        ex.Message));
                }
            }
        }
        
        /// <summary>
        /// 异步获取记录总数
        /// </summary>
        /// <returns>记录总数</returns>
        public Task<int> GetRecordCountAsync()
        {
            return Task.Run(() =>
            {
                if (reader == null)
                    return 0;
                
                reader.EnsureRecordTableIsLoaded();
                return reader.Count;
            });
        }
    }

    public class ARZWriter
    {
        public ARZStrings strtable = null;
        int rcount = 0;
        MemoryStream mrecdata;
        MemoryStream mrectable;
        private BinaryWriter rectableWriter;   // mrectable 的复用写入器（CopyRecord/WriteTombstone 直写用；无缓冲，与 WriteRecord 混用安全）
        ARZHeader hdr;
        public Dictionary<string, TemplateNode> ftemplates = null;
        // 流式模式：不整体拷贝旧数据区，SaveToStream 时按需从源流分块读取（大包保存内存峰值减半）
        private Stream srcStream = null;   // 旧包数据源（只读），调用方负责打开/关闭，本类不释放
        private long srcDataOffset = 0;    // 旧数据区在源流中的起始偏移
        private long srcDataLength = 0;    // 旧数据区长度
        private long newRecordOffsetBase = 0; // 新记录 rdOffset 的基准（流式模式 = 旧数据区长度）
        // 直写模式：数据区边压边写目标流（不在内存累积整包），用于打包/固实/合并等"重建包"场景
        private Stream directStream = null;      // 目标流（可定位可读；本类不释放，生命周期由调用方管理）
        private long directDataStart = ARZHeader.HEADER_SIZE;   // 数据区起点
        private long directPosition = ARZHeader.HEADER_SIZE;    // 数据区当前写入位置
        private Adler32 directDataHash = null;   // 数据区校验和（写数据时接力累计，SaveToStream 无需重读数据区算 hrdata）

        public ARZWriter(Dictionary<string, TemplateNode> atemplates = null)
        {
            ftemplates = atemplates;
            hdr = new ARZHeader();
            strtable = new ARZStrings();
            mrectable = new MemoryStream();
            mrecdata = new MemoryStream();
        }

        /// <summary>
        /// 增量模式构造器：接收旧ARZ的RecordData，新数据将追加到尾部
        /// </summary>
        public ARZWriter(Dictionary<string, TemplateNode> atemplates, byte[] existingRecordData) : this(atemplates)
        {
            if (existingRecordData != null && existingRecordData.Length > 0)
            {
                mrecdata.Write(existingRecordData, 0, existingRecordData.Length);
            }
        }

        /// <summary>
        /// 流式模式构造器：保存时不整体驻留旧 RecordData，而是从源流按块读出直写目标流。
        /// <paramref name="sourceStream"/> 须定位可用（只读即可），生命周期由调用方管理；
        /// 偏移/长度取旧包的数据区（RecordTableStart - HEADER_SIZE）。
        /// </summary>
        public ARZWriter(Dictionary<string, TemplateNode> atemplates, Stream sourceStream, long sourceDataOffset, long sourceDataLength) : this(atemplates)
        {
            srcStream = sourceStream;
            srcDataOffset = sourceDataOffset;
            srcDataLength = sourceDataLength;
            newRecordOffsetBase = sourceDataLength;
        }

        /// <summary>
        /// <b>直写模式</b>构造器：数据区边压边直写 <paramref name="targetStream"/>，不在内存累积整包数据。
        /// 用于打包 / 固实 / 合并等"重建包"场景（内存峰值从"整包"降到"单条记录 + 块级缓冲"）。
        /// <para>要求 <paramref name="targetStream"/> 可定位（CanSeek）且可读（CanRead，SaveToStream 收尾会重读数据区算总校验和）。
        /// 生命周期由调用方管理，本类不释放；构造时会写入 24 字节 header 占位，SaveToStream 时回填真实值。</para>
        /// <para>限制：直写模式没有"旧数据区"，因此 <see cref="CopyRecord"/>/<see cref="WriteTombstone"/>
        /// （引用旧数据区偏移）不可用。</para>
        /// </summary>
        public ARZWriter(Dictionary<string, TemplateNode> atemplates, Stream targetStream) : this(atemplates)
        {
            if (targetStream == null || !targetStream.CanSeek || !targetStream.CanRead)
                throw new ArgumentException("ARZWriter 直写模式要求可定位且可读的流（CanSeek && CanRead）", nameof(targetStream));
            directStream = targetStream;
            directDataHash = new Adler32();
            directDataStart = ARZHeader.HEADER_SIZE;
            directPosition = ARZHeader.HEADER_SIZE;
            // header 占位（此时各表偏移未知，SaveToStream 时 seek 回填）
            targetStream.Seek(0, SeekOrigin.Begin);
            targetStream.Write(new byte[ARZHeader.HEADER_SIZE], 0, ARZHeader.HEADER_SIZE);
        }

        public void BeginWrite()
        {
            // hdr.WriteToStream(ost);
        }

        public void WriteFromRecord(ARZRecord rec)
        {
            ARZRecord clone = new ARZRecord(rec, strtable);
            WriteRecord(clone);
        }

        public void WriteRecord(ARZRecord rec)
        {
            if (directStream != null)
            {
                // 直写模式：记录数据直接写目标流的数据区，偏移以数据区起点为基（rdOffset 语义与其它模式一致）
                int len = (int)Math.Min(rec.rdSizeCompressed, int.MaxValue);
                rec.rdOffset = directPosition - directDataStart;
                directStream.Write(rec.cData, 0, len);
                directDataHash.ComputeHash(rec.cData, 0, len);   // hrdata 接力累计
                directPosition += len;
                rec.WriteToStream(mrectable); // 记录表仍在内存累积（每条几十字节，量级远小于数据区）
                rcount += 1;
                return;
            }
            // 数据区布局 = [旧数据区][新增记录]；流式模式下旧数据区不在 mrecdata 里，需加上其长度作基准
            rec.rdOffset = newRecordOffsetBase + mrecdata.Position;
            mrecdata.Write(rec.cData, 0, (int)Math.Min(rec.rdSizeCompressed, int.MaxValue)); // Write Record Data
            rec.WriteToStream(mrectable); // Write to record table
            rcount += 1;
        }

        public void WriteFromLines(string recordname, string[] lines)
        {
            ARZRecord nrec = new ARZRecord(recordname, lines, ftemplates, strtable);
            WriteRecord(nrec);
        }

        public void WriteFromLines(string recordname, string[] lines, DateTime fileTime)
            => WriteFromLines(recordname, lines, fileTime, null);

        /// <summary>
        /// 由文本行**构造**一条记录（不写入包）：打包 / 编辑器保存 / MOD 合并共用的"构造与写入分离"入口。
        /// 分离的原因：构造阶段才知道"模板未找到"（KeyNotFoundException）与字段级问题
        /// （TemplateSkipped/DataFixes/DataWarnings 挂在记录对象上）——调用方需要先拿到记录对象
        /// 逐条上报，再决定写入或跳过该记录（对齐打包侧"警告后跳过继续"的策略）。
        /// <para>编码语义与 <see cref="WriteFromLines"/> 一致：编码在构造记录**之前**压入
        /// <see cref="ARZStrings.PendingValueEncoding"/>（它参与字符串表去重键），构造完弹出。</para>
        /// </summary>
        public ARZRecord BuildRecord(string recordname, string[] lines, DateTime fileTime, Encoding valueEncoding)
        {
            ARZRecord nrec;
            Encoding saved = strtable.PendingValueEncoding;
            strtable.PendingValueEncoding = valueEncoding ?? saved;
            try { nrec = new ARZRecord(recordname, lines, ftemplates, strtable, fileTime); }
            finally { strtable.PendingValueEncoding = saved; }
            return nrec;
        }

        /// <summary>
        /// 由文本行写入一条记录，并指定**该记录值的写出编码**（<paramref name="valueEncoding"/> 为 null → 默认 UTF-8）。
        /// 传入源文件的实际编码即可实现"源什么编码、包内值就是什么编码"的零转码语义。
        /// </summary>
        public void WriteFromLines(string recordname, string[] lines, DateTime fileTime, Encoding valueEncoding)
        {
            WriteRecord(BuildRecord(recordname, lines, fileTime, valueEncoding));
        }

        /// <summary>
        /// 复制未变化的旧记录：保留指向旧数据区的偏移量
        /// </summary>
        public void CopyRecord(string recordName, ARZReader.ARZRecordInfo oldInfo, ARZStrings oldStrings)
        {
            int newRfid = strtable.AddRecordName(recordName);
            byte[] typeBytes = NameCodec.Acp.GetBytes(oldInfo.rtype);
            // 直接追加到记录表（原实现每条 new MemoryStream + BinaryWriter + ToArray：
            // 16 万条 = 每条 3~4 次分配 + 一次整块拷贝；BinaryWriter 无缓冲，复用同一个即可）
            rectableWriter ??= new BinaryWriter(mrectable, NameCodec.Acp, true);
            rectableWriter.Write(newRfid);
            rectableWriter.Write(typeBytes.Length);
            rectableWriter.Write(typeBytes);
            // 官方格式为无符号u32，写入uint（低32位）
            rectableWriter.Write((uint)oldInfo.rdOffset);
            rectableWriter.Write((uint)oldInfo.rdSizeCompressed);
            rectableWriter.Write((uint)oldInfo.rdSizeDecompressed);
            rectableWriter.Write(oldInfo.rdFileTime.ToFileTimeUtc());
            rcount += 1;
        }

        /// <summary>
        /// 写入墓碑条目：保留旧数据但记录名为空（解包时跳过）
        /// </summary>
        public void WriteTombstone(ARZReader.ARZRecordInfo oldInfo)
        {
            int emptyRfid = strtable.GetEmptyStringId();
            byte[] typeBytes = NameCodec.Acp.GetBytes(oldInfo.rtype);
            // 同 CopyRecord：直接追加，不再经 MemoryStream 中转
            rectableWriter ??= new BinaryWriter(mrectable, NameCodec.Acp, true);
            rectableWriter.Write(emptyRfid);
            rectableWriter.Write(typeBytes.Length);
            rectableWriter.Write(typeBytes);
            // 官方格式为无符号u32，写入uint（低32位）
            rectableWriter.Write((uint)oldInfo.rdOffset);
            rectableWriter.Write((uint)oldInfo.rdSizeCompressed);
            rectableWriter.Write((uint)oldInfo.rdSizeDecompressed);
            rectableWriter.Write(oldInfo.rdFileTime.ToFileTimeUtc());
            rcount += 1;
        }

        public void SaveToStream(Stream ost)
        {
            if (directStream != null)
            {
                SaveToStreamDirect();
                return;
            }
            // Prepare header
            long oldDataLen = srcStream != null ? srcDataLength : 0;
            hdr.RecordTableStart = ARZHeader.HEADER_SIZE + oldDataLen + (int)mrecdata.Length;
            hdr.RecordTableSize = (int)mrectable.Length;
            hdr.RecordTableEntries = rcount;
            MemoryStream mstrtable = new MemoryStream();
            strtable.WriteToStream(mstrtable);
            hdr.StringTableStart = hdr.RecordTableStart + hdr.RecordTableSize;
            hdr.StringTableSize = (int)mstrtable.Length;
            // Write header
            ost.Seek(0, SeekOrigin.Begin);
            MemoryStream mhdr = new MemoryStream(ARZHeader.HEADER_SIZE);
            hdr.WriteToStream(mhdr);

            // Checksums:
            Adler32 hashall = new Adler32();
            byte[] buf = mhdr.GetBuffer();
            hashall.ComputeHash(buf, 0, (int)mhdr.Length);

            uint hrdata;
            uint hrtable;
            uint hstable;
            if (srcStream != null)
            {
                // 流式模式：边写边算校验和，旧数据区分块读取直写目标流，不整体驻留内存。
                // 写出顺序与文件布局一致：header → 旧数据区 → 新增记录 → 记录表 → 字符串表 → footer
                mhdr.WriteTo(ost);
                Adler32 hashData = new Adler32(); // hrdata 需对旧数据区+新增记录接力累计
                byte[] chunk = new byte[1024 * 1024];
                long remaining = srcDataLength;
                srcStream.Seek(srcDataOffset, SeekOrigin.Begin);
                while (remaining > 0)
                {
                    int read = srcStream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                    if (read <= 0)
                        throw new EndOfStreamException("旧包数据区读取提前结束，源文件可能已损坏。");
                    ost.Write(chunk, 0, read);
                    hashall.ComputeHash(chunk, 0, read);
                    hashData.ComputeHash(chunk, 0, read);
                    remaining -= read;
                }
                buf = mrecdata.GetBuffer();
                if (mrecdata.Length > 0)
                {
                    ost.Write(buf, 0, (int)mrecdata.Length);
                    hashall.ComputeHash(buf, 0, (int)mrecdata.Length);
                    hashData.ComputeHash(buf, 0, (int)mrecdata.Length);
                }
                hrdata = hashData.checksum;

                buf = mrectable.GetBuffer();
                ost.Write(buf, 0, (int)mrectable.Length);
                hashall.ComputeHash(buf, 0, (int)mrectable.Length);
                hrtable = (new Adler32()).ComputeHash(buf, 0, (int)mrectable.Length);

                buf = mstrtable.GetBuffer();
                ost.Write(buf, 0, (int)mstrtable.Length);
                hashall.ComputeHash(buf, 0, (int)mstrtable.Length);
                hstable = (new Adler32()).ComputeHash(buf, 0, (int)mstrtable.Length);
            }
            else
            {
                // 内存模式：全部驻留后统一计算并写出（原行为不变）
                buf = mrecdata.GetBuffer();
                hashall.ComputeHash(buf, 0, (int)mrecdata.Length);
                hrdata = (new Adler32()).ComputeHash(buf, 0, (int)mrecdata.Length);
                buf = mrectable.GetBuffer();
                hashall.ComputeHash(buf, 0, (int)mrectable.Length);
                hrtable = (new Adler32()).ComputeHash(buf, 0, (int)mrectable.Length);
                buf = mstrtable.GetBuffer();
                hashall.ComputeHash(buf, 0, (int)mstrtable.Length);
                hstable = (new Adler32()).ComputeHash(buf, 0, (int)mstrtable.Length);

                // Write data
                mhdr.WriteTo(ost);
                mrecdata.WriteTo(ost);
                mrectable.WriteTo(ost);
                mstrtable.WriteTo(ost);
            }

            // Write Footer:
            using (BinaryWriter bw = new BinaryWriter(ost, NameCodec.Acp, true))
            {
                bw.Write(hashall.checksum);
                bw.Write(hstable);
                bw.Write(hrdata);
                bw.Write(hrtable);
            }
        }

        /// <summary>
        /// 直写模式收尾：数据区已在 WriteRecord 时写入目标流，这里回填 header 并追加表/字符串表/footer。
        /// <para>校验和顺序与内存模式一致（header → 数据区 → 记录表 → 字符串表）：
        /// 数据区已落盘，重读一遍分块累计（顺序读、不驻留）；hrdata 用写入时接力累计值，无需重读。</para>
        /// </summary>
        private void SaveToStreamDirect()
        {
            Stream ost = directStream;
            long dataLen = directPosition - directDataStart;

            // header 各字段（数据长度此时已知）
            hdr.RecordTableStart = directDataStart + dataLen;
            hdr.RecordTableSize = (int)mrectable.Length;
            hdr.RecordTableEntries = rcount;
            MemoryStream mstrtable = new MemoryStream();
            strtable.WriteToStream(mstrtable);
            hdr.StringTableStart = hdr.RecordTableStart + hdr.RecordTableSize;
            hdr.StringTableSize = (int)mstrtable.Length;

            // 回填真实 header（占位写在文件开头）
            MemoryStream mhdr = new MemoryStream(ARZHeader.HEADER_SIZE);
            hdr.WriteToStream(mhdr);
            ost.Seek(0, SeekOrigin.Begin);
            mhdr.WriteTo(ost);

            // 总校验和：header
            Adler32 hashall = new Adler32();
            byte[] buf = mhdr.GetBuffer();
            hashall.ComputeHash(buf, 0, (int)mhdr.Length);

            // 总校验和：数据区（已落盘，重读累计；流式分块，不驻留）
            ost.Seek(directDataStart, SeekOrigin.Begin);
            byte[] chunk = new byte[1024 * 1024];
            long remaining = dataLen;
            while (remaining > 0)
            {
                int read = ost.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                if (read <= 0)
                    throw new EndOfStreamException("ARZ 直写收尾校验时数据区读取提前结束。");
                hashall.ComputeHash(chunk, 0, read);
                remaining -= read;
            }
            uint hrdata = directDataHash.checksum;   // 写入数据时已接力累计

            // 追加写记录表 / 字符串表（位置 = 数据区末尾）
            ost.Seek(hdr.RecordTableStart, SeekOrigin.Begin);
            buf = mrectable.GetBuffer();
            ost.Write(buf, 0, (int)mrectable.Length);
            hashall.ComputeHash(buf, 0, (int)mrectable.Length);
            uint hrtable = (new Adler32()).ComputeHash(buf, 0, (int)mrectable.Length);

            buf = mstrtable.GetBuffer();
            ost.Write(buf, 0, (int)mstrtable.Length);
            hashall.ComputeHash(buf, 0, (int)mstrtable.Length);
            uint hstable = (new Adler32()).ComputeHash(buf, 0, (int)mstrtable.Length);

            // footer
            using (BinaryWriter bw = new BinaryWriter(ost, NameCodec.Acp, true))
            {
                bw.Write(hashall.checksum);
                bw.Write(hstable);
                bw.Write(hrdata);
                bw.Write(hrtable);
            }
        }
    }

    public class ARZReader {        
        private Stream fstream = null;
        public ARZHeader hdr = null;
        private ARZStrings strtable = null;
        private List<ARZRecordInfo> recordInfos = null; // 只存储记录的元信息，不加载完整记录
        private Dictionary<int, ARZRecord> loadedRecords = new Dictionary<int, ARZRecord>(); // 已加载的记录缓存
        private Queue<int> recordAccessOrder = new Queue<int>(); // 记录访问顺序，用于LRU缓存
        private const int MAX_LOADED_RECORDS = 1024; // 缓存上限（LRU），0表示无限制；防止批量/搜索触碰大量记录后内存无限增长
        private long recordTablePosition = 0; // 记录表位置，用于流式读取
        private bool isRecordTableLoaded = false; // 记录表是否已加载
        
        public ARZReader(Stream astream)
        {
            fstream = astream;
            Initialize();
        }

        public ARZRecord this[int i] { get { return GetRecord(i); } }

        public int Count { get { EnsureRecordTableLoaded(); return recordInfos != null ? recordInfos.Count : 0; } }

        /// <summary>
        /// 初始化，只读取文件头部信息
        /// </summary>
        private void Initialize()
        {
            // 只读取文件头部
            fstream.Seek(0, SeekOrigin.Begin);
            hdr = new ARZHeader(fstream);
            
            // 布局校验：表偏移必须在文件范围内、字符串表在记录表之后，
            // 防止定位到错误位置导致读取到非法 FileTime
            if (hdr.RecordTableStart < 0 || hdr.RecordTableStart > fstream.Length
                || hdr.StringTableStart < 0 || hdr.StringTableStart > fstream.Length
                || hdr.StringTableStart < hdr.RecordTableStart + hdr.RecordTableSize)
            {
                throw new InvalidDataException(LanguageManager.Instance.GetText("ArzFile.BadStructure",
                    "ARZ 文件结构异常：数据表偏移超出文件范围，文件可能已损坏或不是有效的 ARZ 文件。"));
            }
            
            // 保存记录表位置，但不立即读取
            recordTablePosition = hdr.RecordTableStart;
            
            // 延迟初始化字符串表
            strtable = new ARZStrings(fstream, hdr.StringTableStart, (int)Math.Min(hdr.StringTableSize, int.MaxValue));
        }

        /// <summary>
        /// 确保记录表已加载
        /// </summary>
        private void EnsureRecordTableLoaded()
        {
            if (!isRecordTableLoaded && fstream != null && hdr != null)
            {
                fstream.Seek(recordTablePosition, SeekOrigin.Begin);
                recordInfos = ReadRecordInfoTable(fstream, hdr.RecordTableEntries);
                isRecordTableLoaded = true;
            }
        }
        
        /// <summary>
        /// 公开方法：确保记录表已加载（供外部调用）
        /// </summary>
        public void EnsureRecordTableIsLoaded()
        {
            EnsureRecordTableLoaded();
        }
        
        /// <summary>
        /// 读取记录信息表，只存储元数据，不创建完整的ARZRecord对象
        /// </summary>
        private List<ARZRecordInfo> ReadRecordInfoTable(Stream astream, int rcount)
        {
            List<ARZRecordInfo> infoList = new List<ARZRecordInfo>();
            infoList.Capacity = (int)rcount;
            
            // 保存当前流位置
            long currentPosition = astream.Position;
            
            using (BinaryReader br = new BinaryReader(astream, NameCodec.Acp, true))
            {
                for (int i = 0; i < rcount; i++)
                {
                    // 只读取记录的元数据，不创建完整的ARZRecord对象
                    int rfid = br.ReadInt32();
                    int rtypelen = br.ReadInt32();
                    // 记录类型串按本机 ACP 解码（与记录名/WriteRecord 同一口径）
                    byte[] typeBytes = br.ReadBytes(rtypelen);
                    string rtype = NameCodec.Acp.GetString(typeBytes);
                    // 读取为uint（无符号）后存储为long，偏移可超过2GB
                    long rdOffset = br.ReadUInt32();
                    long rdSizeCompressed = br.ReadUInt32();
                    long rdSizeDecompressed = br.ReadUInt32();
                    DateTime rdFileTime = DateTime.FromFileTimeUtc(br.ReadInt64());
                    
                    infoList.Add(new ARZRecordInfo(rfid, rtype, rdOffset, rdSizeCompressed, rdSizeDecompressed, rdFileTime));
                }
            }
            
            // 恢复流位置
            astream.Seek(currentPosition, SeekOrigin.Begin);
            return infoList;
        }
        
        /// <summary>
        /// 获取原始RecordData字节
        /// </summary>
        public byte[] GetRawRecordData()
        {
            long dataLength = hdr.RecordTableStart - ARZHeader.HEADER_SIZE;
            if (dataLength <= 0) return new byte[0];
            fstream.Seek(ARZHeader.HEADER_SIZE, SeekOrigin.Begin);
            byte[] data = new byte[(int)Math.Min(dataLength, int.MaxValue)];
            fstream.Read(data, 0, (int)Math.Min(dataLength, int.MaxValue));
            return data;
        }

        /// <summary>
        /// 获取记录名→元信息的映射（跳过空名记录）
        /// </summary>
        public Dictionary<string, ARZRecordInfo> GetRecordInfoMap()
        {
            EnsureRecordTableLoaded();
            strtable.EnsureLoaded();
            var map = new Dictionary<string, ARZRecordInfo>();
            for (int i = 0; i < recordInfos.Count; i++)
            {
                var info = recordInfos[i];
                if (info.rfid < 0) continue;
                string name = strtable[info.rfid];
                if (string.IsNullOrEmpty(name)) continue;
                if (!map.ContainsKey(name))
                    map.Add(name, info);
            }
            return map;
        }

        /// <summary>
        /// 按记录名获取已加载完整数据的记录（用于合并时从包内原样复制）。
        /// infoMap 由 GetRecordInfoMap 提供；加载后 rec.entries / rec.cData 均已就绪。
        /// </summary>
        public ARZRecord GetLoadedRecordByName(string name, Dictionary<string, ARZRecordInfo> infoMap)
        {
            if (infoMap == null) return null;
            if (!infoMap.TryGetValue(name, out ARZRecordInfo info)) return null;
            ARZRecord rec = info.CreateRecord(GetStringTable());
            LoadRecordData(rec);
            return rec;
        }

        /// <summary>
        /// <b>逐条流式遍历</b>有效记录（跳过墓碑 / 空名 / 重名），回调提供解压后数据。
        /// 与批量取全部数据的区别：回调返回后该条数据即可被回收，**不会把全部记录的解压数据
        /// 同时驻留内存**（大包固实 / 重建包场景专用；旧实现 GetAllRecordsWithData 会一次解压全部）。
        /// </summary>
        /// <param name="action">参数：记录名、元信息、解压后数据（回调内请勿长期持有该数组）</param>
        public void ForEachRecordData(Action<string, ARZRecordInfo, byte[]> action)
        {
            if (action == null) return;
            EnsureRecordTableLoaded();
            strtable.EnsureLoaded();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < recordInfos.Count; i++)
            {
                var info = recordInfos[i];
                string name = strtable[info.rfid];
                if (string.IsNullOrEmpty(name)) continue;
                if (!seen.Add(name)) continue;
                // 无缓存读取（不写入 loadedRecords），避免记录缓存被铺满
                ARZRecord record = GetRecord(i, fstream);
                byte[] decompressed = DecompressRecordData(record);
                record.DiscardData();
                action(name, info, decompressed);
            }
        }

        /// <summary>
        /// 解压记录数据并返回原始字节（不修改record内部状态）
        /// </summary>
        private byte[] DecompressRecordData(ARZRecord record)
        {
            if (record.cData == null || record.rdSizeCompressed <= 0 || record.rdSizeDecompressed <= 0)
                return new byte[0];
            try
            {
                byte[] decompressed = new byte[(int)Math.Min(record.rdSizeDecompressed, int.MaxValue)];
                int len = LZ4Codec.Decode(record.cData, 0, (int)record.rdSizeCompressed,
                                           decompressed, 0, (int)record.rdSizeDecompressed);
                if (len != record.rdSizeDecompressed) return new byte[0];
                return decompressed;
            }
            catch
            {
                return new byte[0];
            }
        }

        /// <summary>
        /// 按记录名获取解压后的原始数据字节（先按需加载压缩数据，再解压，随后释放）
        /// 用于对比编辑等功能；记录名不区分大小写。找不到返回 null。
        /// </summary>
        public byte[] GetDecompressedDataByName(string recordName)
        {
            if (recordName == null || strtable == null) return null;
            EnsureRecordTableLoaded();
            strtable.EnsureLoaded();
            for (int i = 0; i < recordInfos.Count; i++)
            {
                var info = recordInfos[i];
                if (info.rfid < 0) continue;
                string name = strtable[info.rfid];
                if (name == null) continue;
                if (!string.Equals(name, recordName, StringComparison.OrdinalIgnoreCase)) continue;
                ARZRecord record = GetRecord(i);
                byte[] data = DecompressRecordData(record);
                record.DiscardData();
                return data;
            }
            return null;
        }

        /// <summary>
        /// 按记录名获取已解析为 DBR 文本行的内容（先加载记录再通过 SaveToStream 转文本）。
        /// 与 GetDecompressedDataByName 不同：后者返回解压后的二进制条目字节（非文本）。
        /// 用于对比编辑等需要可读文本的场景；记录名不区分大小写。找不到返回 null。
        /// </summary>
        public string GetDecompressedTextByName(string recordName)
        {
            if (recordName == null || strtable == null) return null;
            EnsureRecordTableLoaded();
            strtable.EnsureLoaded();
            for (int i = 0; i < recordInfos.Count; i++)
            {
                var info = recordInfos[i];
                if (info.rfid < 0) continue;
                string name = strtable[info.rfid];
                if (name == null) continue;
                if (!string.Equals(name, recordName, StringComparison.OrdinalIgnoreCase)) continue;
                ARZRecord record = GetRecord(i);
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        record.SaveToStream(ms);
                        ms.Position = 0;
                        using (var sr = new StreamReader(ms, new UTF8Encoding(false)))
                            return sr.ReadToEnd();
                    }
                }
                finally
                {
                    record.DiscardData();
                }
            }
            return null;
        }

        public ARZStrings GetStringTable() { return strtable; }

        /// <summary>
        /// 记录元信息类，用于存储记录的基本信息而不加载完整数据
        /// </summary>
        public class ARZRecordInfo
        {
            public int rfid;
            public string rtype;
            public long rdOffset;
            public long rdSizeCompressed;
            public long rdSizeDecompressed;
            public DateTime rdFileTime;
            
            public ARZRecordInfo(int rfid, string rtype, long rdOffset, long rdSizeCompressed, long rdSizeDecompressed, DateTime rdFileTime)
            {
                this.rfid = rfid;
                this.rtype = rtype;
                this.rdOffset = rdOffset;
                this.rdSizeCompressed = rdSizeCompressed;
                this.rdSizeDecompressed = rdSizeDecompressed;
                this.rdFileTime = rdFileTime;
            }
            
            public ARZRecord CreateRecord(ARZStrings strtable)
            {
                ARZRecord record = new ARZRecord();
                record.rfid = rfid;
                record.rtype = rtype;
                record.rdOffset = rdOffset;
                record.rdSizeCompressed = rdSizeCompressed;
                record.rdSizeDecompressed = rdSizeDecompressed;
                record.rdFileTime = rdFileTime;
                record.strtable = strtable;
                return record;
            }

            public void WriteToStream(Stream stream)
            {
                using (BinaryWriter bw = new BinaryWriter(stream, NameCodec.Acp, true))
                {
                    byte[] typeBytes = NameCodec.Acp.GetBytes(rtype);
                    bw.Write(rfid);
                    bw.Write(typeBytes.Length);
                    bw.Write(typeBytes);
                    // 写入uint（低32位），与官方无符号字段一致
                    bw.Write((uint)rdOffset);
                    bw.Write((uint)rdSizeCompressed);
                    bw.Write((uint)rdSizeDecompressed);
                    bw.Write(rdFileTime.ToFileTimeUtc());
                }
            }
        }

        public ARZRecord GetRecord(int id) {            
            EnsureRecordTableLoaded();
            
            // 检查记录是否已经加载
            if (loadedRecords.TryGetValue(id, out ARZRecord loadedRecord))
            {
                // 更新记录访问顺序
                UpdateRecordAccessOrder(id);
                return loadedRecord;
            }
            
            // 按需创建并加载单个记录
            ARZRecordInfo info = recordInfos[id];
            ARZRecord record = info.CreateRecord(strtable);
            
            // 加载记录数据
            LoadRecordData(record);
            
            // 不限制缓存大小，只管理访问顺序用于LRU缓存（当需要手动清理时使用）
            // 当MAX_LOADED_RECORDS为0时不进行缓存清理
            
            // 缓存已加载的记录
            loadedRecords[id] = record;
            recordAccessOrder.Enqueue(id);
            EvictOldRecords();

            return record;
        }

        /// <summary>
        /// 并行只读扫描用：使用外部数据流加载记录，不触碰实例流与加载缓存（线程安全）。
        /// 调用前请确保元数据已预热（EnsureRecordTableIsLoaded / EnsureStringTableLoadedForParallel）。
        /// </summary>
        public ARZRecord GetRecord(int id, Stream stream)
        {
            EnsureRecordTableLoaded();
            if (strtable != null) strtable.EnsureLoaded();
            ARZRecordInfo info = recordInfos[id];
            ARZRecord record = info.CreateRecord(strtable);
            stream.Seek(ARZHeader.HEADER_SIZE + record.rdOffset, SeekOrigin.Begin);
            using (BinaryReader br = new BinaryReader(stream, NameCodec.Acp, true))
                record.ReadData(br);
            return record;
        }

        /// <summary>
        /// 预热字符串表与记录表，供并行只读扫描前一次性加载（之后可安全共享只读元数据）。
        /// </summary>
        public void EnsureStringTableLoadedForParallel()
        {
            EnsureRecordTableLoaded();
            if (strtable != null) strtable.EnsureLoaded();
        }

        // 原方法：一次性获取所有记录名称
        public List<string> GetAllRecordNames()
        {
            List<string> recordNames = new List<string>();
            EnsureRecordTableLoaded();
            if (recordInfos == null || strtable == null)
                return recordNames;

            // 确保字符串表已加载
            strtable.EnsureLoaded();

            foreach (var recordInfo in recordInfos)
            {
                // 通过字符串表获取记录名称
                string recordName = strtable[recordInfo.rfid];
                recordNames.Add(recordName);
            }
            return recordNames;
        }
        
        // 新增：分批获取记录名称，避免一次性加载所有名称导致内存不足
        public IEnumerable<string> GetRecordNamesInBatches(int batchSize = 1000)
        {
            EnsureRecordTableLoaded();
            if (recordInfos == null || strtable == null)
                yield break;

            // 确保字符串表已加载
            strtable.EnsureLoaded();

            // 分批处理记录名称
            List<string> batch = new List<string>(batchSize);
            for (int i = 0; i < recordInfos.Count; i++)
            {
                string recordName = strtable[recordInfos[i].rfid];
                batch.Add(recordName);
                
                // 当批次达到指定大小时，返回批次数据
                if (batch.Count >= batchSize)
                {
                    foreach (string name in batch)
                    {
                        yield return name;
                    }
                    batch.Clear();
                }
            }
            
            // 返回剩余的记录名称
            foreach (string name in batch)
            {
                yield return name;
            }
        }
        
        // 新增：通过索引范围获取记录名称
        public List<string> GetRecordNamesByRange(int startIndex, int count)
        {
            List<string> recordNames = new List<string>();
            EnsureRecordTableLoaded();
            if (recordInfos == null || strtable == null)
                return recordNames;

            // 确保字符串表已加载
            strtable.EnsureLoaded();
            
            // 计算实际的结束索引
            int endIndex = Math.Min(startIndex + count, recordInfos.Count);
            
            // 只加载指定范围的记录名称
            for (int i = startIndex; i < endIndex; i++)
            {
                string recordName = strtable[recordInfos[i].rfid];
                recordNames.Add(recordName);
            }
            
            return recordNames;
        }
        
        // 新增：通过索引获取单个记录名称
        public string GetRecordName(int index)
        {
            EnsureRecordTableLoaded();
            if (recordInfos == null || strtable == null || index < 0 || index >= recordInfos.Count)
                return string.Empty;

            // 确保字符串表已加载
            strtable.EnsureLoaded();
            
            // 获取指定索引的记录名称
            int rfid = recordInfos[index].rfid;
            if (rfid < 0) return string.Empty;
            return strtable[rfid];
        }

        // 新增：按需获取记录，避免一次性加载所有记录数据
        /// <summary>
        /// 获取记录元信息（不加载记录数据）
        /// </summary>
        public ARZRecordInfo GetRecordInfo(int id)
        {
            EnsureRecordTableLoaded();
            if (id < 0 || id >= recordInfos.Count) return null;
            return recordInfos[id];
        }

        public ARZRecord GetRecordLazy(int id)
        {
            EnsureRecordTableLoaded();
            
            // 检查记录是否已经加载
            if (loadedRecords.TryGetValue(id, out ARZRecord loadedRecord))
            {
                // 更新记录访问顺序
                UpdateRecordAccessOrder(id);
                return loadedRecord;
            }
            
            // 按需创建单个记录，但不加载数据
            ARZRecordInfo info = recordInfos[id];
            ARZRecord record = info.CreateRecord(strtable);
            
            // 不限制缓存大小，当MAX_LOADED_RECORDS为0时不进行缓存清理
            
            // 缓存已创建但未加载数据的记录
            loadedRecords[id] = record;
            recordAccessOrder.Enqueue(id);
            EvictOldRecords();

            return record;
        }

        /// <summary>
        /// LRU 淘汰：缓存超过上限时移除最久未访问的记录。
        /// 仅从缓存移除，记录仍可按需从包内重新解压加载，数据不受影响；
        /// 同时根治 DiscardData 后空记录残留缓存的问题。
        /// </summary>
        private void EvictOldRecords()
        {
            // 每个缓存中的 id 都在队列里（UpdateRecordAccessOrder 已去重），队列耗尽前必能降到上限内
            while (loadedRecords.Count > MAX_LOADED_RECORDS && recordAccessOrder.Count > 0)
            {
                int oldest = recordAccessOrder.Dequeue();
                if (loadedRecords.ContainsKey(oldest))
                    loadedRecords.Remove(oldest);
            }
        }

        // 更新记录访问顺序，用于LRU缓存策略
        private void UpdateRecordAccessOrder(int recordId)
        {
            // 创建一个新的队列，移除旧的记录ID并添加到队列末尾
            Queue<int> newOrder = new Queue<int>();
            
            foreach (int id in recordAccessOrder)
            {
                if (id != recordId)
                    newOrder.Enqueue(id);
            }
            
            // 将当前记录ID添加到队列末尾
            newOrder.Enqueue(recordId);
            
            // 更新访问顺序队列
            recordAccessOrder = newOrder;
        }

        // 新增：按需加载记录数据
        public void LoadRecordData(ARZRecord record)
        {
            if (record.entries == null)
            {
                // 确保字符串表已加载，因为记录数据可能引用字符串表
                if (strtable != null)
                {
                    strtable.EnsureLoaded();
                }
                
                fstream.Seek(ARZHeader.HEADER_SIZE + record.rdOffset, SeekOrigin.Begin);
                using (BinaryReader br = new BinaryReader(fstream, NameCodec.Acp, true))
                    record.ReadData(br);
            }
        }

    }

    public class ARZStrings {
        private List<string> strtable = null;
        private Dictionary<string, int> strsearchlist = null;
        private Stream stream = null;
        private long stringTableStart = 0;
        private int stringTableSize = 0;
        private bool isStringTableLoaded = false;

        /// <summary>
        /// 记录名索引集合（唯一真源）：命中者写出时按【本机 ACP】编码，其余项按【值编码】写出。
        /// 依据：官方工具链 = 内容零转码、**只有文件名跟本机 ACP**（见 skill §6.4/§6.5，字节级实测）。
        /// 记录名来自文件系统路径、字段值来自文件内容，两者是**独立编码**，不得混用（skill §1.2 R2.4）。
        /// </summary>
        private readonly HashSet<int> recordNameIds = new HashSet<int>();

        /// <summary>与 strtable 平行的"值写出编码"表；记录名项不看此表（恒用本机 ACP 写出）</summary>
        private readonly List<Encoding> valueEncodings = new List<Encoding>();

        /// <summary>
        /// 【去重键 → 表项 id】，键 = **该表项将要写出的字节序列**（用 Latin1 与之双向一一映射成 string）。
        /// <para>去重键必须是"最终字节"而不是"文本"或"文本+编码名"：</para>
        /// <para>· 用**文本**去重 → 同一句话在 GBK 源与 UTF-8 源里会撞成一条，只能存一种编码（原样档串味）；</para>
        /// <para>· 用**文本+编码名**去重 → 纯 ASCII 字符串在 ACP 与 UTF-8 下字节完全相同却算两条，
        ///   于是包里那些"既是记录名、又被别的记录当字段值引用"的路径会各存一份（实测 1929 条多余、
        ///   包体大 6%）；</para>
        /// <para>· 用**字节序列**去重 → 与官方语义一致（官方就是按包内字节去重）：ASCII 自然合并，
        ///   真正字节不同的（中文 GBK vs UTF-8）才分开。</para>
        /// 懒建（首次用到时按现有表项初始化），此后只在新增表项时增量更新；整表重建时置 null。
        /// </summary>
        private Dictionary<string, int> encodedIds;

        /// <summary>把任意字节序列映射为可作字典键的字符串（Latin1 对 0~255 是双射，无碰撞）</summary>
        private static readonly Encoding ByteKeyEncoding = Encoding.Latin1;

        /// <summary>去重键分隔符：不可能出现在记录名/字段值中的控制字符</summary>
        private const char KeySeparator = '\0';

        /// <summary>
        /// 计算表项去重键：**该表项将要写出的字节序列**。
        /// <para>有损编码（本机码页表示不了的字会被替换成 '?'）会让**不同文本**产生**相同字节**，
        /// 若只用字节作键就会把两条不同内容静默并成一条 —— 这类项额外把原文本并入键，
        /// 保证"能合并的才合并，合不了的不硬合"。</para>
        /// </summary>
        private static string MakeDedupKey(string text, Encoding enc)
        {
            byte[] bytes = enc.GetBytes(text);
            // 快速路径：纯 ASCII 文本在单/多字节编码下均**无损**（字节数 = 字符数且逐字节同值）
            // → 键就是字节串本身，省掉两次 GetString 解码。dbr 的字段名与多数值都走这条
            //（16 万记录 × 20 字段 = 240 万次调用，省下的分配很可观）。
            if (IsAsciiRoundTrip(bytes, text))
                return ByteKeyEncoding.GetString(bytes);
            string key = ByteKeyEncoding.GetString(bytes);
            if (!string.Equals(enc.GetString(bytes), text, StringComparison.Ordinal))
                key = key + KeySeparator + text;
            return key;
        }

        /// <summary>字节序列是否为该文本的 ASCII 无损往返（逐字节 = 字符且都在 ASCII 范围）</summary>
        private static bool IsAsciiRoundTrip(byte[] bytes, string text)
        {
            if (bytes.Length != text.Length) return false;
            for (int i = 0; i < bytes.Length; i++)
                if (bytes[i] > 0x7F || bytes[i] != (byte)text[i]) return false;
            return true;
        }

        /// <summary>
        /// 【打包中】接下来由 <see cref="AddString"/> 新增的表项所用的**值编码**；
        /// null = 用 <see cref="DefaultValueEncoding"/>。调用方在构造每条记录前设置、构造完置回 null。
        /// 它**同时参与去重键**，这是"逐条保真"能成立的前提（见 <see cref="encodedIds"/> 说明）。
        /// </summary>
        public Encoding PendingValueEncoding { get; set; }

        /// <summary>默认值编码：UTF-8 无 BOM（表项未被显式指定编码时使用）</summary>
        private static readonly Encoding DefaultValueEncoding = new UTF8Encoding(false);

        /// <summary>表项数（不触发延迟加载；供打包流程记录"本记录新增了哪些表项"用）</summary>
        public int RawCount => strtable?.Count ?? 0;

        /// <summary>
        /// 取第 <paramref name="i"/> 项的**包内原始编码**（唯一真源，写表与"原样解包"共用）。
        /// <para>记录名 → <see cref="NameCodec.Acp"/>（官方工具链只有文件名跟本机 ACP）。</para>
        /// <para>其余项 → 该项读入时判定出的编码 / 写入时显式指定的编码；未标记则 UTF-8 无 BOM。</para>
        /// </summary>
        public Encoding GetItemEncoding(int i)
        {
            if (recordNameIds.Contains(i)) return NameCodec.Acp;
            return (i >= 0 && i < valueEncodings.Count && valueEncodings[i] != null)
                ? valueEncodings[i]
                : DefaultValueEncoding;
        }

        public ARZStrings()
        {
            strtable = new List<string>();
        }

        public ARZStrings(Stream astream, int size) : this()
        {
            ReadStream(astream, size);
        }
        
        /// <summary>
        /// 延迟加载构造函数
        /// </summary>
        /// <param name="astream">文件流</param>
        /// <param name="start">字符串表开始位置</param>
        /// <param name="size">字符串表大小</param>
        public ARZStrings(Stream astream, long start, int size) : this()
        {
            stream = astream;
            stringTableStart = start;
            stringTableSize = size;
            isStringTableLoaded = false;
        }

        public string this[int i] 
        { 
            get 
            { 
                EnsureStringTableLoaded();
                if (i < 0 || i >= strtable.Count) 
                {
                    Program.Log.Error(LanguageManager.Instance.GetText("ArzStrings.IndexOutOfRange",
                        "ARZStrings: 索引越界 - 索引={0}, 表大小={1}"), i, strtable.Count);
                    return string.Format("[Invalid String: index={0}]", i);
                }
                return strtable[i]; 
            } 
            set 
            { 
                EnsureStringTableLoaded();
                if (i < 0 || i >= strtable.Count) 
                {
                    Program.Log.Error(LanguageManager.Instance.GetText("ArzStrings.IndexOutOfRange",
                        "ARZStrings: 索引越界 - 索引={0}, 表大小={1}"), i, strtable.Count);
                    return;
                }
                strtable[i] = value; 
            } 
        }

        public int Count 
        { 
            get 
            { 
                EnsureStringTableLoaded();
                return strtable.Count; 
            } 
        }
        
        /// <summary>
        /// 确保字符串表已加载
        /// </summary>
        private void EnsureStringTableLoaded()
        {
            if (!isStringTableLoaded && stream != null)
            {
                stream.Seek(stringTableStart, SeekOrigin.Begin);
                ReadStreamInternal(stream, stringTableSize);
                isStringTableLoaded = true;
            }
        }
        
        /// <summary>
        /// 公共方法：确保字符串表已加载
        /// </summary>
        public void EnsureLoaded()
        {
            EnsureStringTableLoaded();
        }
        
        public void ReadStream(Stream astream, int size)
        {
            stream = astream;
            stringTableStart = astream.Position;
            stringTableSize = size;
            ReadStreamInternal(astream, size);
            isStringTableLoaded = true;
        }
        
        private void ReadStreamInternal(Stream astream, int size)
        {
            if (strtable == null)
                strtable = new List<string>();
            strtable.Capacity = 0;
            valueEncodings.Clear();
            // 整表重建 → 按文本 / 按"文本+编码"的两张查找表都作废，改为下次访问时重建
            strsearchlist = null;
            encodedIds = null;
            int pos = 0;
            using (BinaryReader br = new BinaryReader(astream, NameCodec.Acp, true))
            {
                try
                {
                    while (pos < size)
                    {
                        // 安全检查：确保有足够的字节读取count
                        if (pos + 4 > size)
                        {
                            Program.Log.Error(LanguageManager.Instance.GetText("ArzStrings.CountTruncated",
                                "ARZStrings: 字符串表数据格式错误，剩余字节不足读取count"));
                            break;
                        }
                        
                        int count = br.ReadInt32(); pos += 4; // Read Count
                        
                        // 不限制count值（只检查负数）
                        if (count < 0)
                        {
                            Program.Log.Error(LanguageManager.Instance.GetText("ArzStrings.BadCount",
                                "ARZStrings: 发现异常的count值: {0}"), count);
                            break;
                        }
                        
                        strtable.Capacity += count;
                        
                        for (int i = 0; i < count; i++)
                        {
                            // 安全检查：确保有足够的字节读取length
                            if (pos + 4 > size)
                            {
                                Program.Log.Error(LanguageManager.Instance.GetText("ArzStrings.LengthTruncated",
                                    "ARZStrings: 字符串表数据格式错误，剩余字节不足读取length"));
                                break;
                            }
                            
                            int length = br.ReadInt32(); pos += 4;
                            
                            // 不限制length值（只检查负数）
                            if (length < 0)
                            {
                                Program.Log.Error(LanguageManager.Instance.GetText("ArzStrings.BadLength",
                                    "ARZStrings: 发现异常的length值: {0}"), length);
                                break;
                            }
                            
                            // 安全检查：确保有足够的字节读取字符数据
                            if (pos + length > size)
                            {
                                Program.Log.Error(LanguageManager.Instance.GetText("ArzStrings.CharDataTruncated",
                                    "ARZStrings: 字符串表数据格式错误，剩余字节不足读取字符数据"));
                                // 调整pos到流的末尾，退出循环
                                pos = size;
                                break;
                            }
                            
                            // 使用正确的编码读取字符串
                            byte[] bytes = br.ReadBytes(length);
                            pos += length;
                            
                            try
                            {
                                // 解码，并**记下这条实际用到的编码** —— 增量打包 / 编辑器回写时可据此保真
                                string str = DecodeArzString(bytes, out Encoding usedEnc);
                                strtable.Add(str);
                                valueEncodings.Add(usedEnc);
                            }
                            catch (Exception ex)
                            {
                                Program.Log.Error(LanguageManager.Instance.GetText("ArzStrings.DecodeFailed",
                                    "ARZStrings: 解码字符串时出错: {0}"), ex.Message);
                                // 添加一个占位符字符串，继续处理
                                strtable.Add("[解码错误]");
                                valueEncodings.Add(DefaultValueEncoding);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Log.Error(LanguageManager.Instance.GetText("ArzStrings.ReadFailed",
                        "ARZStrings: 读取字符串表时发生错误: {0}"), ex.Message);
                }
            }
        }

        public void WriteToStream(Stream astream)
        {
            using (BinaryWriter bw = new BinaryWriter(astream, NameCodec.Acp, true))
            {
                bw.Write((int)strtable.Count);
                for (int i = 0; i < strtable.Count; i++)
                {
                    string s = strtable[i];
                    // 记录名 → 本机 ACP（与官方工具一致）；其余（字段名 / 字符串值）→ 该项的值编码
                    byte[] bytes = GetItemEncoding(i).GetBytes(s);
                    // 长度必须与写入字节同源（按字节数，而非字符数）
                    bw.Write(bytes.Length);
                    bw.Write(bytes);
                }
            }
        }

        public int AddString(string newvalue)
        {
            if (strsearchlist == null || encodedIds == null) // Build string search list on first access
                BuildStringSearchList();

            // 去重键 = 该表项**将要写出的字节序列**（与官方"按包内字节去重"的语义一致）
            Encoding enc = PendingValueEncoding ?? DefaultValueEncoding;
            string key = MakeDedupKey(newvalue, enc);
            if (encodedIds.TryGetValue(key, out int existing))
                return existing;

            strtable.Add(newvalue);
            valueEncodings.Add(enc);                    // 与 strtable 保持平行（记录名/值同表）
            encodedIds[key] = strtable.Count - 1;
            // 按【文本】的查找表保留首条即可（同文各表项内容一致，取哪条都等价）
            if (!strsearchlist.ContainsKey(newvalue))
                strsearchlist.Add(newvalue, strtable.Count - 1);
            return strtable.Count - 1;
        }

        /// <summary>
        /// 按【记录名】加入字符串表：写出时用**本机 ACP**编码（官方工具链的格式约定）。
        /// 记录名与字段值同处一张表、但编码规则不同 —— 这里打标记，WriteToStream 据此分别编码。
        /// 去重也按 ACP 走：与同为 ACP 的值项共用表项是**字节等价**的（都按 ACP 写出），无副作用。
        /// </summary>
        public int AddRecordName(string recordName)
        {
            Encoding saved = PendingValueEncoding;
            PendingValueEncoding = NameCodec.Acp;
            int id;
            try { id = AddString(recordName); }
            finally { PendingValueEncoding = saved; }
            recordNameIds.Add(id);
            return id;
        }

        public int GetStringID(string searchstr)
        {
            if (strsearchlist == null)
                BuildStringSearchList();

            if (strsearchlist.ContainsKey(searchstr))
                return strsearchlist[searchstr];
            else
                return -1;
        }

        public void BuildStringSearchList()
        {
            // Dictionary（非 SortedDictionary）：本表只做"按文本查表项下标"，无顺序依赖，
            // 哈希查找与构建都更快、内存更省（几十万表项时差别明显）
            strsearchlist = new Dictionary<string, int>(strtable.Count);
            encodedIds = new Dictionary<string, int>(strtable.Count);
            for (int i = 0; i < strtable.Count; i++)
            {
                // 文本可能重复（同文不同编码各占一条表项）→ 按【文本】的查找表保留首条
                if (!strsearchlist.ContainsKey(strtable[i]))
                    strsearchlist.Add(strtable[i], i);
                string key = MakeDedupKey(strtable[i], GetItemEncoding(i));
                if (!encodedIds.ContainsKey(key))
                    encodedIds[key] = i;
            }
        }

        /// <summary>
        /// 从旧字符串表预填充，保留所有项（包括重复项），保持索引一致供CopyRecord使用
        /// </summary>
        public void PrepopulateFromOldTable(ARZStrings oldTable)
        {
            EnsureStringTableLoaded();
            oldTable.EnsureStringTableLoaded();

            for (int i = 0; i < oldTable.Count; i++)
            {
                strtable.Add(oldTable[i]);
                // 跟随旧表该项的编码（包内判定所得），使复制过来的旧记录在新包里保持原编码
                valueEncodings.Add(i < oldTable.valueEncodings.Count && oldTable.valueEncodings[i] != null
                    ? oldTable.valueEncodings[i] : DefaultValueEncoding);
            }

            // 两张查找表统一重建（含"文本+编码"去重键表），避免手写同步漏项
            BuildStringSearchList();
        }

        private int emptyStringId = -1;

        /// <summary>
        /// 获取空字符串在字符串表中的ID（用于tombstone标记）
        /// </summary>
        public int GetEmptyStringId()
        {
            if (emptyStringId < 0)
            {
                int id = GetStringID("");
                if (id >= 0)
                    emptyStringId = id;
                else
                    emptyStringId = AddString("");
            }
            return emptyStringId;
        }

        /// <summary>
        /// 解码 ARZ 字符串：**字节级严格 UTF-8** 优先，不合法时回退**本机 ACP**（简中机 = GBK）。
        /// 原实现用宽松解码后查 U+FFFD —— 既会让"GBK 双字节恰好是合法 UTF-8"的条目静默误判为 UTF-8，
        /// 也会把"内容本身含 U+FFFD 字符"的合法 UTF-8 误判为非 UTF-8；改用严格字节校验后两者均修正。
        /// 兜底编码由固定的 GBK 改为**本机 ACP**：中文机行为不变，日文/繁体机才能正确兼容本地编码的包。
        /// </summary>
        private static string DecodeArzString(byte[] bytes) => DecodeArzString(bytes, out _);

        /// <summary>同上，并回带**实际用到的编码**（供表项编码记录 / 增量保真使用）</summary>
        private static string DecodeArzString(byte[] bytes, out Encoding usedEncoding)
        {
            if (bytes == null || bytes.Length == 0) { usedEncoding = Encoding.UTF8; return string.Empty; }
            if (TextFileHelper.IsValidUtf8(bytes)) { usedEncoding = Encoding.UTF8; return Encoding.UTF8.GetString(bytes); }
            usedEncoding = NameCodec.Acp;
            return NameCodec.Acp.GetString(bytes);
        }

    }

    public class ARZRecord
    {
        public int rfid;
        public string rtype;
        public long rdOffset;
        public long rdSizeCompressed;
        public long rdSizeDecompressed;
        public DateTime rdFileTime;
        public byte[] cData;
        public byte[] aData;
        public List<ARZEntry> entries = null;
        public ARZStrings strtable = null;
        private HashSet<string> entryset = null;
        public string Name { get { return strtable != null && rfid >= 0 && rfid < strtable.Count ? strtable[rfid] : string.Format("[Invalid Name: rfid={0}]", rfid); } }

        /// <summary>
        /// 模板里**找不到**、因此**不会写进包**的字段清单（"字段名 = 值"）。
        /// 由文本构造器填充（打包时由调用方统一上报为字段级警告）。
        /// 复制构造器（ARZRecord(tocopy,…)）不走模板，故无此信息。
        /// </summary>
        public List<string> TemplateSkipped;

        /// <summary>
        /// 打包/保存时**已自动修复**的行清单（补尾逗号 / 收敛多余尾逗号 / 清理首尾空白）。
        /// 由文本构造器填充，调用方统一上报为警告级日志并做结尾汇总。
        /// </summary>
        public List<string> DataFixes;

        /// <summary>
        /// 打包/保存时的**可疑行**清单（行无逗号 / 值含逗号 / 疑似粘字段名 / 行尾多余内容 / 非法分号 等）。
        /// 由文本构造器填充，调用方统一上报为警告级日志并做结尾汇总。
        /// </summary>
        public List<string> DataWarnings;

        /// <summary>单行判定结果（打包/保存侧 DBR 行规范化）</summary>
        private sealed class LineCheck
        {
            /// <summary>规范化后的行（可能已修复；Skip 时无意义）</summary>
            internal string Line;
            /// <summary>是否跳过该行（不解析、不写入）</summary>
            internal bool Skip;
            /// <summary>跳过时是否完全静默（合法空值 / 只有 key）</summary>
            internal bool Silent;
            /// <summary>已修复说明（逐条记录：同一行可能同时清空白 + 补逗号，各报一条）</summary>
            internal readonly List<string> Fixes = new List<string>();
            /// <summary>警告说明（同一行可并存多条，如 值含逗号 + 值含控制字符）</summary>
            internal readonly List<string> Warnings = new List<string>();
        }

        /// <summary>
        /// DBR 行规范化与判定（打包 / 编辑器保存共用）。
        /// 依据：DBR 行格式为 `字段名,值,`（无引号、无转义，行尾逗号为**终止符**；值里不可能含逗号）；
        /// 解析器按逗号**顺序**分段（第 1 段=字段名、第 2 段=值）。
        /// 处置：能确定性修复的就修（补/收敛尾逗号、清理首尾空白）；语义不可判定的跳过并报告。
        /// <para>注：曾实现过"值尾疑似粘字段名"检测，因值与字段名撞车率过高（如 ach070Title）、
        /// 误报无法接受，已按使用者要求移除。</para>
        /// </summary>
        private static LineCheck CheckDbrLine(string rawLine)
        {
            var r = new LineCheck();
            string line = rawLine.Trim();                    // 行首尾空白清理
            bool trimmed = line != rawLine;
            // 日志用文本：控制字符可视化 + 超长值裁切（字段名在行首，始终完整）；
            // 否则 defaultRecipes 这类几十条路径的数组值会把日志刷爆
            string lineForLog = LogService.TruncateValueForLog(rawLine);

            // 统一内容检测（与检测按钮/解包同一套 DbrContentCheck）：值含控制字符 → 出现即报告、按原样保留
            // （行首尾的制表符已由下方空白清理处理并报"空白清理"，此处针对清理后仍存在的控制字符）
            if (DbrContentCheck.HasControlChar(line))
                r.Warnings.Add(string.Format(LanguageManager.Instance.GetText("LOG.DbrControlCharWarn",
                    "值含控制字符，已按原样保留：\"{0}\""), lineForLog));

            string[] parts = line.Split(',');
            int n = parts.Length;

            // 有效段数 e：去掉尾部连续空段；tail = 尾部空段个数
            int e = n;
            while (e > 0 && parts[e - 1].Trim().Length == 0) e--;
            int tail = n - e;

            string key = parts[0].Trim();
            string val = n >= 2 ? parts[1].Trim() : "";
            if (trimmed || key != parts[0] || (n >= 2 && val != parts[1]))
                r.Fixes.Add(string.Format(LanguageManager.Instance.GetText("LOG.DbrFixedWhitespace",
                    "字段行首尾空白已清理：\"{0}\""), lineForLog));

            if (n == 1)
            {
                r.Skip = true;
                r.Warnings.Add(string.Format(LanguageManager.Instance.GetText("LOG.DbrLineNoComma",
                    "字段行没有逗号，已跳过：\"{0}\""), lineForLog));
                return r;
            }
            if (key.Length == 0)
            {
                r.Skip = true;
                r.Warnings.Add(string.Format(LanguageManager.Instance.GetText("LOG.DbrEmptyKeySkipped",
                    "字段名为空，已跳过：\"{0}\""), lineForLog));
                return r;
            }

            if (n == 2)
            {
                if (val.Length == 0)
                {
                    r.Skip = true;                            // `k,`：只有 key，无值 → 跳过不提示
                    r.Silent = true;
                    return r;
                }
                r.Line = key + "," + val + ",";               // 修复：补行尾逗号
                r.Fixes.Add(string.Format(LanguageManager.Instance.GetText("LOG.DbrFixedMissingComma",
                    "字段行末尾缺逗号，已补上：\"{0}\""), lineForLog));
                return r;
            }

            // n >= 3
            // ⚠️ 顺序要紧：`k,,`（合法空值）必须先于"多余尾逗号"判定，否则会被误收敛成 `k,,`（改写数据）
            if (e == 1 && tail == 2)
            {
                r.Skip = true;                                // `k,,`：合法空值（字段不写入包）
                r.Silent = true;
                return r;
            }
            if (e <= 2 && tail >= 2)
            {
                // 多余尾逗号 → 收敛为一个（值部分保留）：`k,v,,` → `k,v,`；`k,,,` → `k,,`
                r.Line = e >= 2 && parts[1].Trim().Length > 0 ? key + "," + val + "," : key + ",,";
                r.Fixes.Add(string.Format(LanguageManager.Instance.GetText("LOG.DbrFixedExtraComma",
                    "字段行有多余尾逗号，已收敛：\"{0}\""), lineForLog));
                return r;
            }
            if (e == 2 && tail == 1)
            {
                // 正常形态 `k,v,`
                r.Line = key + "," + val + ",";
                return r;
            }

            // e >= 3：行内多组内容 / 值含逗号（`k,v,aaa`、`k,v,v,a,e`、`k,v1,v2,` 等）
            // 统一按**既有解析口径**处理：取第 2 段作值、其余内容忽略（照旧写入），仅提示
            r.Line = key + "," + val + ",";
            r.Warnings.Add(string.Format(LanguageManager.Instance.GetText("LOG.DbrCommaInValue",
                "值含逗号或行内有多组内容，已按第 2 段写入、其余忽略（值=\"{1}\"）：\"{0}\""),
                lineForLog, LogService.TruncateValueForLog(val)));
            return r;
        }

        private static ARZEntryComparer NameComparer = new ARZEntryComparer();
   
        // 添加默认构造函数，用于按需创建记录对象
        public ARZRecord()
        {
            // 初始化必要的字段为默认值
            rfid = -1;
            rtype = string.Empty;
            rdOffset = 0;
            rdSizeCompressed = 0;
            rdSizeDecompressed = 0;
            rdFileTime = DateTime.MinValue;
            cData = null;
            aData = null;
            entries = null;
            strtable = null;
            entryset = null;
        }
        
        /// <summary>
        /// 非标准模板路径提示（打包 / 编辑器保存共用）。
        /// <para>只**提示**，不改变解析结果 —— 模板按规范化后的键正常使用、字段照常写入。
        /// 同一种写法在进程内只提示一次，避免整包刷屏（去重键 = 归一化原路径 + 命中的规范键）。</para>
        /// </summary>
        private static readonly HashSet<string> _nonStandardTplNotified = new HashSet<string>(StringComparer.Ordinal);

        private static void NotifyNonStandardTemplate(string rawName, string standardKey)
        {
            string key = TemplateNameResolver.Normalize(rawName) + " → " + standardKey;
            lock (_nonStandardTplNotified)
                if (!_nonStandardTplNotified.Add(key)) return;
            Program.Log.Warn(string.Format(LanguageManager.Instance.GetText("LOG.TemplateNonStandardPathPack",
                "非标准模板路径（已按规范路径使用，可能有字段缺失）：{0} → {1}"), rawName, standardKey));
        }

        public ARZRecord(string rname, string[] rstrings, Dictionary<string, TemplateNode> templates, ARZStrings astrtable, DateTime fileTime = default)
        {
            strtable = astrtable;
            rfid = strtable.AddRecordName(rname);
            rtype = ""; // TODO: IMPORTANT: How record type is determined, last piece of info
            rdFileTime = fileTime == default ? DateTime.Now : fileTime;
            entries = new List<ARZEntry>();
            entryset = new HashSet<string>();
            TemplateNode tpl = null;
            foreach (string rawLine in rstrings) {
                // 容忍首尾空白：` templateName,x.dbr,` 也应能识别模板（否则整条记录会报"无模板"）
                string line = rawLine?.Trim();
                if (line != null && line.StartsWith("templateName"))
                {
                    string[] eexpl = line.Split(',');
                    try
                    {
                        string templateName = eexpl[1];

                        // 模板查找（**全项目唯一口径** TemplateNameResolver）：支持非标准写法
                        // （如 mods/<mod名>/database/templates/x.tpl —— AM 也能识别，一般可用）。
                        // 旧实现只试"原样 → 补 database/templates/ 前缀"，非标准写法一律找不到
                        // → 打包时该整文件被当作"模板未找到"丢掉（实测 ReignOfTerror.arz 有 1 条）。
                        var tplRes = TemplateNameResolver.Resolve(templates, templateName);
                        tpl = tplRes.Template;
                        if (tpl != null && tplRes.NonStandard)
                            NotifyNonStandardTemplate(templateName, tplRes.StandardKey);

                        // 如果找到了模板
                        if (tpl != null)
                        {
                            ARZEntry newentry = new ARZEntry(eexpl[0], TemplateNode.TemplateNameVar, rname, this);
                            if (newentry.TryAssign(eexpl[0], eexpl[1], strtable, ""))
                            {
                                entries.Add(newentry);
                                entryset.Add(eexpl[0]);
                            }
                        }
                        else
                        {
                            // 模板未找到，抛出异常
                            throw new KeyNotFoundException(string.Format("Template '{0}' not found", templateName));
                        }
                    }
                    catch (KeyNotFoundException)
                    {
                        // 修复：将参数整合到字符串插值中
                        Program.Log.Error($"{LanguageManager.Instance.GetText("LOG.ErrorTemplateNotFound", "使用的模板未找到：")} \"{eexpl[1]}\"");
                        //throw e;
                        throw;
                    }
                    break;
                }
            }
            if (tpl == null)
            {
                Program.Log.Error("Record {0} has no template!", rname); // DEBUG
                throw new Exception(string.Format("Record {0} has no template!", rname));
            }
            foreach (string rawEstr in rstrings)
            {
                if (rawEstr == null) continue;
                var chk = CheckDbrLine(rawEstr);
                if (chk.Silent) continue;                       // 合法空值（k,,）/ 只有 key（k,）：跳过且不提示
                if (chk.Skip)
                {
                    (DataWarnings ??= new List<string>()).AddRange(chk.Warnings);
                    continue;
                }
                if (chk.Fixes.Count > 0) (DataFixes ??= new List<string>()).AddRange(chk.Fixes);
                if (chk.Warnings.Count > 0) (DataWarnings ??= new List<string>()).AddRange(chk.Warnings);

                // 规范化后的行（修复形态必为 `字段名,值,`；解析口径：第 1 段=字段名、第 2 段=值）
                string estr = chk.Line;
                TemplateNode vart = null;
                string[] eexpl = estr.Split(',');
                string varname = eexpl[0];
                string vvalue = eexpl.Length > 1 ? eexpl[1] : "";
                string defaultsto = "";
                if (varname == "templateName")
                {
                    continue; // templateName should have been assigned already
                }
                else
                {
                    // Find variable in templates
                    // TODO: All this is rubbish, introduces ambiguity, find root cause, not hack & tack
                    vart = tpl.FindVariable(varname); 
                    if (vart != null)
                    {
                        if (vart.values.ContainsKey("defaultValue"))
                            defaultsto = vart.values["defaultValue"];

                        // 非数组、非字符串类型（如 int/real/bool）的值里含分号：
                        // 后续 TryAssign 会**静默截断**（只保留第一段、丢弃其余）→ 按定案改为跳过该字段并报告。
                        // 数组（class=array）用 `;` 作分隔符、字符串类型整串保留，均属正常，不拦。
                        {
                            bool isArrayField = vart.values.ContainsKey("class") && vart.values["class"] == "array";
                            string ftype = vart.values.ContainsKey("type") ? vart.values["type"] : "";
                            bool isStringLike = ftype == "string" || ftype == "equation"
                                || (!string.IsNullOrEmpty(ftype) && ftype.StartsWith("file_"));
                            if (!isArrayField && !isStringLike && vvalue.IndexOf(';') >= 0)
                            {
                                // 非数组、非字符串类型：分号不是分隔符，值会被**截取第一段**（既有行为，保持不变）
                                // 这里只补一条提示，让"悄悄丢数据"变得可见
                                (DataWarnings ??= new List<string>()).Add(string.Format(
                                    LanguageManager.Instance.GetText("LOG.DbrSemicolonTruncated",
                                        "记录 {0} 的字段 {1} 不是数组，值含分号：已按第一段写入、其余截断：\"{2}\""),
                                    rname, varname, LogService.TruncateValueForLog(vvalue)));
                            }
                        }
                        if (entryset.Contains(varname))
                        {
                            // 重复字段：**保持既有行为（后一条覆盖前一条）**，仅把日志从 Info 升为警告并走多语言
                            Program.Log.Warn(LanguageManager.Instance.GetText("LOG.DbrDuplicateEntry",
                                "记录 {0} 的字段 {1} 重复出现：后一条覆盖前一条"), rname, varname);
                            ARZEntry entry = entries.Find(e => e.Name == varname);
                            entry.TryAssign(varname, vvalue, strtable, defaultsto);
                            continue;
                        }
                        else
                        {

                            if (varname.ToLower() == "class") rtype = vvalue;
                            ARZEntry newentry = new ARZEntry(varname, vart, rname, this);
                            if (newentry.TryAssign(varname, vvalue, strtable, defaultsto))
                            {
                                entries.Add(newentry);
                                entryset.Add(varname);
                            }
                        }
                    }
                    else {
                        // 模板里没有该字段 → **不会写进包里**。原先只记 Debug（界面/CLI 都看不到），
                        // 用户要求打包时给出字段级日志，故记录到记录对象上，由打包流程统一上报。
                        // 不再另记一条 Debug —— 字段级警告已含同样信息，重复输出反而干扰阅读
                        (TemplateSkipped ??= new List<string>()).Add($"{varname} = {vvalue}");
                        // notfoundvars.Add(varname);
                    }
                }
                    
            }
            // 字段写出顺序 = **按字段名 ordinal 升序**（官方行为，实测官方包内 3146/3146 记录严格升序）。
            // 第 0 项恒为 templateName，不参与排序。
            if (entries.Count > 1)
                entries.Sort(1, entries.Count - 1, NameComparer); // Sort all except first "templateName" entry
            PackData();
        }

        public ARZRecord(ARZRecord tocopy, ARZStrings astrtable) {
            strtable = astrtable;
            rfid = strtable.AddRecordName(tocopy.Name);
            rtype = tocopy.rtype;
            rdFileTime = tocopy.rdFileTime;
            entries = new List<ARZEntry>();
            foreach (ARZEntry tce in tocopy.entries)
            {
                entries.Add(new ARZEntry(tce, this));
            }
            PackData();
        }

        public ARZRecord(BinaryReader rdata, ARZStrings astrtable)
        {
            strtable = astrtable;
            ReadBytes(rdata);
        }

        public void ReadBytes(BinaryReader rdata)
        {
            // read record info
            rfid = rdata.ReadInt32();
            // string record_file = strtable[rfid];
            int rtypelen = rdata.ReadInt32();
            // 记录类型串按本机 ACP 解码，与 WriteRecord 写出口径匹配
            byte[] typeBytes = rdata.ReadBytes(rtypelen);
            rtype = NameCodec.Acp.GetString(typeBytes);
            rdOffset = rdata.ReadUInt32();
            rdSizeCompressed = rdata.ReadUInt32();
            rdSizeDecompressed = rdata.ReadUInt32();
            rdFileTime = DateTime.FromFileTimeUtc(rdata.ReadInt64());
        }

        public List<ARZEntry> ReadData(BinaryReader brdata) // Reads record entry data from reader and creates entries list
        {
            try
            {
                // 安全检查：确保压缩和解压缩大小合理（不限制最大大小）
                if (rdSizeCompressed <= 0 || rdSizeDecompressed <= 0)
                {
                    Program.Log.Error(LanguageManager.Instance.GetText("ArzRecord.BadSize",
                        "ARZRecord: 发现异常的大小值 - 压缩: {0}, 解压: {1}"), rdSizeCompressed, rdSizeDecompressed);
                    entries = new List<ARZEntry>();
                    return entries;
                }
                
                cData = brdata.ReadBytes((int)Math.Min(rdSizeCompressed, int.MaxValue));
                
                // 安全检查：确保读取的字节数正确
                if (cData.Length != rdSizeCompressed)
                {
                    Program.Log.Error(LanguageManager.Instance.GetText("ArzRecord.CompressedLengthMismatch",
                        "ARZRecord: 读取的压缩数据长度不匹配 - 预期: {0}, 实际: {1}"), rdSizeCompressed, cData.Length);
                    entries = new List<ARZEntry>();
                    return entries;
                }
                
                // 使用标准老式LZ4Codec.Decode方法解压数据
                try
                {
                    aData = new byte[(int)Math.Min(rdSizeDecompressed, int.MaxValue)];
                    // 使用LZ4Codec.Decode进行解压
                    int decompressedLength = LZ4Codec.Decode(cData, 0, (int)rdSizeCompressed, aData, 0, (int)rdSizeDecompressed);
                    
                    // 验证解压结果
                    if (decompressedLength != rdSizeDecompressed)
                    {
                        throw new Exception(string.Format(
                            LanguageManager.Instance.GetText("ArzRecord.DecompressedLengthMismatch",
                                "解压长度不匹配: 预期 {0}, 实际 {1}"),
                            rdSizeDecompressed, decompressedLength));
                    }
                }
                catch (Exception ex)
                {
                    // 带上记录名：否则检测/解包日志里只有异常文本，无法定位是哪条记录坏了
                    Program.Log.Error(LanguageManager.Instance.GetText("ArzRecord.DecompressFailed",
                        "ARZRecord: 解压数据时出错: {0}（记录: {1}）"), ex.Message, Name);
                    entries = new List<ARZEntry>();
                    return entries;
                }
                
                entries = new List<ARZEntry>();
                // 设置合理的容量预估（不限制条目数量）
                int estimatedCapacity = (int)Math.Min(rdSizeDecompressed / 12, int.MaxValue); // 假设每个条目至少12字节
                entries.Capacity = estimatedCapacity;
                
                using (MemoryStream eMem = new MemoryStream(aData))
                {
                    using (BinaryReader eDbr = new BinaryReader(eMem, NameCodec.Acp, true))
                    {
                        int entryCount = 0;
                        while (eMem.Position < eMem.Length)
                        {
                            // 不限制条目数量
                            
                            // 安全检查：确保至少有8字节可读取（ARZEntry头部大小）
                            if (eMem.Position + 8 > eMem.Length)
                            {
                                Program.Log.Error(LanguageManager.Instance.GetText("ArzRecord.NotEnoughData",
                                    "ARZRecord: 剩余数据不足，无法读取完整条目"));
                                break;
                            }
                            
                            try
                            {
                                ARZEntry entry = new ARZEntry(eDbr, this);
                                entries.Add(entry);
                                entryCount++;
                            }
                            catch (Exception ex)
                            {
                                Program.Log.Error(LanguageManager.Instance.GetText("ArzRecord.CreateEntryFailed",
                                    "ARZRecord: 创建条目时出错: {0}"), ex.Message);
                                // 尝试跳过当前条目，继续处理下一个
                                break; // 由于无法确定当前条目的大小，最好的做法是中断处理
                            }
                        }
                    }
                }
                
                // 清理内存
                aData = null;
                
                return entries;
            }
            catch (Exception ex)
            {
                Program.Log.Error(LanguageManager.Instance.GetText("ArzRecord.ReadRecordFailed",
                    "ARZRecord: 读取记录数据时发生错误: {0}"), ex.Message);
                entries = new List<ARZEntry>();
                return entries;
            }
        }

        /// <summary>
        /// 获取解压后的原始数据字节（不修改内部状态）
        /// </summary>
        public byte[] GetRawDecompressedData()
        {
            if (cData == null || rdSizeCompressed <= 0 || rdSizeDecompressed <= 0)
                return null;
            try
            {
                byte[] decompressed = new byte[(int)Math.Min(rdSizeDecompressed, int.MaxValue)];
                int len = LZ4Codec.Decode(cData, 0, (int)rdSizeCompressed, decompressed, 0, (int)rdSizeDecompressed);
                if (len != rdSizeDecompressed) return null;
                return decompressed;
            }
            catch
            {
                return null;
            }
        }

        public void DiscardData()
        {
            entries = null;
            cData = null;
        }

        public void PackData()
        {
            int datasize = entries.Count * 8; // Headers
            foreach (ARZEntry e in entries)
                datasize += e.values.Length * 4; // + Data
            using (MemoryStream mStream = new MemoryStream(datasize))
            {
                using (BinaryWriter bWriter = new BinaryWriter(mStream, NameCodec.Acp, true))
                {
                    mStream.Seek(0, SeekOrigin.Begin);
                    foreach (ARZEntry e in entries)
                    {
                        // e.dcount;
                        e.dcount = (ushort)e.values.Length;
                        // Console.WriteLine("Packing {0} - Len: {1} ", Program.strtable[e.dstrid], e.dcount); // DEBUG
                        e.WriteBytes(bWriter);
                    }
                }

                // Replace data
                aData = mStream.GetBuffer();
                rdSizeDecompressed = aData.Length;
                
                // 使用LZ4Codec.Encode进行压缩
                try
                {
                    // 为压缩数据分配足够的空间
                    cData = new byte[LZ4Codec.MaximumOutputSize((int)Math.Min(rdSizeDecompressed, int.MaxValue))];
                    
                    // 使用LZ4Codec.Encode进行压缩
                    int compressedLength = LZ4Codec.Encode(aData, 0, (int)rdSizeDecompressed, cData, 0, cData.Length);
                    
                    // 如果实际压缩大小小于分配的空间，调整数组大小
                    if (compressedLength < cData.Length)
                    {
                        byte[] temp = new byte[compressedLength];
                        Buffer.BlockCopy(cData, 0, temp, 0, compressedLength);
                        cData = temp;
                    }
                }
                catch (Exception ex)
                {
                    Program.Log.Error(LanguageManager.Instance.GetText("ArzRecord.CompressFailed",
                        "ARZRecord: 压缩数据时出错: {0}"), ex.Message);
                    cData = new byte[0];
                    rdSizeCompressed = 0;
                }
                
                aData = null;
                rdSizeCompressed = cData.Length;
            }
        }

        /// <summary>默认输出编码：UTF-8 无 BOM（内存序列化路径的内部约定，见 SaveToStream 说明）</summary>
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public void SaveToFile(string filename)
        {
            using (FileStream fs = new FileStream(filename, FileMode.Create))
                SaveToStream(fs);
        }

        /// <summary>
        /// 把记录序列化为 DBR 文本行写入流。无参重载 = UTF-8 无 BOM（**内存序列化**的内部约定，
        /// 其产物只在内存中流转、随即由同一编码读回，不落盘也不进包）。
        /// </summary>
        public void SaveToStream(Stream astream) => SaveToStream(astream, null);

        /// <summary>
        /// 把记录序列化为 DBR 文本行写入流，可指定输出编码。
        /// <para><paramref name="encoding"/> 为 null → UTF-8 无 BOM（同无参重载）。</para>
        /// <para>**解包 / 导出等"落盘"路径必须传入目标编码**：本方法的产物是直接写盘的
        /// （见 GDPackEditorAPI.UnpackArz），若固定 UTF-8，解包产物就会恒为 UTF-8，
        /// 与官方"包内什么编码、解出来就是什么编码"的原样行为不一致。</para>
        /// <para><paramref name="onContentIssue"/>：解包等落盘路径传入回调（参数1=问题种类
        /// <see cref="DbrContentCheck"/> Kind*，参数2为"记录 → 字段"定位文本），
        /// 每发现一处调用一次——供调用方逐条显示 + 结束分类汇总（可选，默认 null 不影响其它调用点）。</para>
        /// </summary>
        public void SaveToStream(Stream astream, Encoding encoding, Action<string, string> onContentIssue = null)
        {
            using (StreamWriter sr = new StreamWriter(astream, encoding ?? Utf8NoBom, -1, true))
            {
                sr.NewLine = "\n";
                foreach (ARZEntry etr in entries)
                {
                    string estring = etr.ToString();
                    string entryName = rfid >= 0 && strtable != null ? strtable[etr.dstrid] : null;
                    if (estring.Contains('\n') || estring.Contains(Environment.NewLine))
                    {
                        if (rfid >= 0 && strtable != null)
                        {
                            string recName = strtable[rfid];
                            Program.Log.Warn(LanguageManager.Instance.GetText("LOG.RecordNewlineFix",
                                "记录 \"{0}\" 的字段 \"{1}\" 值含换行，已删除换行（值可能与相邻内容粘连）"), recName, entryName);
                            onContentIssue?.Invoke(DbrContentCheck.KindNewline, recName + " → " + entryName);
                        }
                        estring = System.Text.RegularExpressions.Regex.Replace(estring, @"\r\n?|\n", "");
                    }
                    // 统一内容检测：逗号/控制字符按原样保留、仅报告（与检测按钮/解包/打包同一套判定，零分配）
                    int first = estring.IndexOf(','), last = estring.LastIndexOf(',');
                    if (rfid >= 0 && strtable != null && first >= 0)
                    {
                        string val = last > first ? estring.Substring(first + 1, last - first - 1) : "";
                        var flags = DbrContentCheck.CheckFlags(val) & ~DbrContentFlags.Newline;   // 换行已在上方处理
                        string recName = strtable[rfid];
                        // 报告顺序与 Check 一致：逗号 → 控制字符
                        if ((flags & DbrContentFlags.Comma) != 0)
                        {
                            Program.Log.Warn(LanguageManager.Instance.GetText("LOG.RecordCommaWarn",
                                "记录 \"{0}\" 的字段 \"{1}\" 值含逗号，已按原样保留"), recName, entryName);
                            onContentIssue?.Invoke(DbrContentCheck.KindComma, recName + " → " + entryName);
                        }
                        if ((flags & DbrContentFlags.ControlChar) != 0)
                        {
                            Program.Log.Warn(LanguageManager.Instance.GetText("LOG.RecordControlCharWarn",
                                "记录 \"{0}\" 的字段 \"{1}\" 值含控制字符，已按原样保留"), recName, entryName);
                            onContentIssue?.Invoke(DbrContentCheck.KindControlChar, recName + " → " + entryName);
                        }
                    }
                    sr.WriteLine(estring);
                }
            }
        }

        /// <summary>
        /// 【原样档 / 官方行为】把记录序列化为 DBR 文本行，**每项按其包内原始编码**写出（<b>零转码</b>）。
        /// <para>解包 / 导出等"落盘"路径用它：包内值是什么编码，磁盘上就还原成什么编码，
        /// 与官方 AssetManager 的"字段值字节原样落盘"一致（plan F6 / G1）。</para>
        /// <para>不经过 <see cref="SaveToStream(Stream, Encoding)"/> 的原因：后者只能给整个文件挑一种编码，
        /// 而一条记录内字段名与各字段值的编码可以互不相同。</para>
        /// </summary>
        public void SaveToStreamPreservingEncodings(Stream astream, Action<string, string> onContentIssue = null)
        {
            var buf = new List<byte>(8192);
            foreach (ARZEntry etr in entries)
            {
                buf.Clear();
                etr.AppendLineBytes(buf, onContentIssue);
                buf.Add((byte)'\n');
                astream.Write(CollectionsMarshal.AsSpan(buf));
            }
        }

        public void WriteToStream(Stream rtstream)
        {
            using (BinaryWriter bw = new BinaryWriter(rtstream, NameCodec.Acp, true))
            {
                WriteRecord(bw, rdOffset);
            }
        }

        public void WriteRecord(BinaryWriter rtable, long dataoffset)
        {
            rdOffset = dataoffset;
            rtable.Write(rfid);
            // 按本机 ACP 计算类型串实际字节数
            byte[] typeBytes = NameCodec.Acp.GetBytes(rtype);
            rtable.Write(typeBytes.Length);
            rtable.Write(typeBytes);
            // 官方格式为无符号u32，写入uint（低32位）
            rtable.Write((uint)rdOffset);
            rtable.Write((uint)rdSizeCompressed);
            rtable.Write((uint)rdSizeDecompressed);
            rtable.Write(rdFileTime.ToFileTimeUtc());
        }

    }

    public enum ARZEntryType : ushort { Int=0, Real=1, String=2, Bool=3 };

    public class ARZEntryComparer : IComparer<ARZEntry>
    {
        public int Compare(ARZEntry first, ARZEntry second) 
        {
              return String.CompareOrdinal(first.Name, second.Name);
        }
    }


    public class ARZEntry
    {
        public ARZEntryType dtype;
        public ushort dcount;
        public int dstrid;
        public int[] values;
        public bool changed = false; // TODO: overhead
        public bool isarray = false;
        public bool isfile = false;
        private ARZRecord parent = null;
        // private ARZStrings strtable = null;
        // static SortedList<string, int> strsearchlist = null;
        public ARZStrings StrTable { get { return parent?.strtable; } }
        public string Name { get { return StrTable[dstrid]; } }
        // public static SortedList<string, int> StrSearchList { get { if (strsearchlist == null) strsearchlist = Program.strsearchlist; return strsearchlist; } set { strsearchlist = value; } }

        public ARZEntry(ARZEntry tocopy, ARZRecord aparent)
        {
            parent = aparent;
            dstrid = StrTable.AddString(tocopy.Name);
            dtype = tocopy.dtype;
            dcount = tocopy.dcount;
            values = (int[])tocopy.values.Clone();
            if (dtype == ARZEntryType.String)
                for (int i = 0; i < values.Length; i++) {
                    values[i] = StrTable.AddString(tocopy.AsString(i));
                }
        }

        public ARZEntry(BinaryReader edata, ARZRecord aparent)
        {
            parent = aparent;            
            ReadBytes(edata);
        }

        public ARZEntry(string entryname, TemplateNode tpl, string recname, ARZRecord aparent)
        {
            string vtype = null;
            parent = aparent;

            try
            {
                vtype = tpl.values["type"];
            }
            catch (KeyNotFoundException)
            {
                Program.Log.Error("Template {0} does not contain value type for entry {1}! I'm not guessing it.", tpl.GetTemplateFile(), entryname);
                throw;
            }

            isarray = tpl.values.ContainsKey("class") && tpl.values["class"] == "array"; // This is an array act accordingly when packing strings
                        
            if (vtype.StartsWith("file_"))
            {
                vtype = "file"; // Make it string
                isfile = true;
            }

            switch (vtype)
            {
                case "string":
                case "file":
                case "equation":
                    dtype = ARZEntryType.String; // string type
                    break;
                case "real":
                    dtype = ARZEntryType.Real;
                    break;
                case "bool":
                    dtype = ARZEntryType.Bool;
                    break;
                case "int":
                    dtype = ARZEntryType.Int;
                    break;
                default:
                    Program.Log.Error("Template {0} has unknown type {1} for entry {1}", tpl.GetTemplateFile(), tpl.values["type"], entryname);
                    throw new Exception("Unknown variable type");
                    // break;
            }

            values = new int[0];
            dstrid = StrTable.AddString(entryname);

        }

        public void ReadBytes(BinaryReader edata)
        {
            // header
            dtype = (ARZEntryType)edata.ReadUInt16();
            dcount = edata.ReadUInt16();
            dstrid = edata.ReadInt32();
            // read all entries
            values = new int[dcount];
            for (int i = 0; i < dcount; i++)
            {
                values[i] = edata.ReadInt32();
            }
        }

        public void WriteBytes(BinaryWriter edata)
        {
            edata.Write((ushort)dtype);
            edata.Write(dcount);
            edata.Write(dstrid);
            foreach (int v in values)
                edata.Write(v);
        }

        public int AsInt(int eid)
        {
            return values[eid];
        }

        public float AsFloat(int eid)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(values[eid]), 0);
        }

        /// <summary>
        /// 把 Real（float）条目的原始位模式按**官方工具（C++ <c>%.6f</c>）**的语义格式化为 6 位小数。
        /// <para>⚠ 必须**先提升为 double 再格式化**。.NET Core 3.0 起浮点 ToString 走"最短可往返表示"：
        /// float 会先被压成 <c>36.9</c> 再补零 → <c>36.900000</c>；而官方是按真实二进制值
        /// <c>36.90000152587890625</c> 四舍五入 → <c>36.900002</c>。两者对不上。</para>
        /// <para>提升为 double 后，最短可往返表示需要 ~17 位有效数字（远超 6 位），
        /// 四舍五入结果即与官方一致。全程只做一次转换，不改变存包的位模式。</para>
        /// </summary>
        private static string FormatReal(int value)
        {
            return ((double)BitConverter.ToSingle(BitConverter.GetBytes(value), 0)).ToString("0.000000");
        }

        public string AsString(int eid)
        {
            return AsStringEx(eid, StrTable);
        }

        public string AsStringEx(int eid, ARZStrings strtable)
        {
            return strtable[values[eid]];
        }

        public bool TryAssign(string entryname, string valuestr, ARZStrings strtable, string defaultsto = "") {
            if (strtable[dstrid] != entryname)
            {
                Program.Log.Warn("Cannot assign \"{0}\" to \"{1}\" field (entry names differ).", entryname, strtable[dstrid]);
                return false;
            }
            if (string.IsNullOrWhiteSpace(valuestr))
            {
                valuestr = defaultsto;
                return false;
            }

            if (isfile) valuestr = valuestr.Replace(Path.DirectorySeparatorChar, '/').ToLower();
            string[] strs = valuestr.Split(';');
            if (!isarray )
            {
                if (strs.Length > 1)
                {
                    if (dtype == ARZEntryType.String)
                    {
                        strs = new string[1] { valuestr };
                    }
                    else
                    {
                        strs = new string[1] { strs[0] };
                    }
                }
            }
            else if (valuestr.Contains(";;"))
            {
                // This is when it get's weird:
                // try compacting multiple empty strings to ;;                
                List<string> cstrs = new List<string>();
                string accum = "";
                for (int i = 0; i < strs.Length; i++) // 
                {
                    if (strs[i] == "")
                    {
                        if (i + 1 < strs.Length && strs[i + 1] == "") accum += ";"; // Ignore last ; as it is a separator
                    }
                    else
                    {
                        if (accum != "")
                        {
                            // if (dtype != ARZEntryType.Real)
                            cstrs.Add(accum);
                            accum = "";
                        }
                        //if (dtype == ARZEntryType.Real && !string.IsNullOrWhiteSpace(strs[i]))
                        cstrs.Add(strs[i]);
                    }
                }
                // if (accum != "" && dtype != ARZEntryType.Real)
                if (accum != "")
                        cstrs.Add(accum);
                strs = cstrs.ToArray<string>();
            }

            float fval = (float)0.0;
            int[] nvalues = new int[strs.Length];

            for (int i = 0; i < strs.Length; i++)
            {               
                switch (dtype)
                {
                    case ARZEntryType.Int: // TODO: Move entry types to static Consts for clarity
                        if (!int.TryParse(strs[i], out nvalues[i]))
                        {
                            if (float.TryParse(strs[i], out fval))
                            {
                                nvalues[i] = (int)fval;
                                // Console.WriteLine("Int value represented as float {0} in {1}, truncating", fval, parent.Name); // DEBUG
                                Program.Log.Debug("Int value represented as float \"{0:F}\" in {1}/{2}, truncating", fval, parent.Name, entryname);
                            } else
                            if (strs[i].StartsWith("0x"))
                            {
                                try
                                {
                                    nvalues[i] = Convert.ToInt32(strs[i].Substring(2), 16);
                                }
                                catch
                                {
                                    // Console.WriteLine("Could not parse Hex number {0}", strs[i]); // DEBUG
                                    Program.Log.Debug("Could not parse Hex number \"{0}\" in {1}/{2}", strs[i], parent.Name, entryname);
                                    nvalues[i] = 0;
                                }
                            }
                            else
                            {
                                // DEBUG:
                                // Console.WriteLine("Record {3} Entry {0} Error parsing integer value #{1}=\"{2}\", Defaulting to 0", Name, i, strs[i], parent.Name);
                                // return false;
                                Program.Log.Debug("Error parsing integer value \"{0}\" in {1}/{2}", strs[i], parent.Name, entryname);
                                nvalues[i] = 0; // Set default
                            }
                        }
                        break;
                    case ARZEntryType.Real:
                        if (!float.TryParse(strs[i], out fval))
                        {
                            Program.Log.Debug("Error parsing float value \"{0}\" in {1}/{2}, Defaulting to 0.0", strs[i], parent.Name, entryname);
                            nvalues[i] = BitConverter.ToInt32(BitConverter.GetBytes(0.0), 0);
                        }
                        nvalues[i] = BitConverter.ToInt32(BitConverter.GetBytes(fval), 0);
                        break;
                    case ARZEntryType.String: // String
                        { 
                            nvalues[i] = strtable.AddString(strs[i]);
                        }
                        break;
                    case ARZEntryType.Bool:
                        if (!int.TryParse(strs[i], out nvalues[i]) || nvalues[i] > 1)
                        {
                            Program.Log.Debug("Error parsing boolean value \"{0}\" in {1}/{2}, Defaulting to False", strs[i], parent.Name, entryname);
                            nvalues[i] = 0;
                            // return false;
                        }
                        break;
                    default:
                        Program.Log.Warn("Unknown data type {2} for entry {0}/{1}", parent.Name, entryname, dtype);
                        return false;
                }
            }
            values = nvalues;
            dcount = (ushort)values.Length;
            changed = true;
            return true;
        }

        public override string ToString()
        {
            if (StrTable == null) return "";
            StringBuilder sb = new StringBuilder();
            sb.Append(StrTable[dstrid]).Append(',');
            bool firstentry = true;
            foreach (int value in values)
            {
                if (!firstentry) sb.Append(";");
                switch (dtype)
                {
                    case ARZEntryType.Int:
                    case ARZEntryType.Bool:
                    default:
                        sb.Append(value); // values are signed!
                        break;
                    case ARZEntryType.Real:
                        sb.Append(FormatReal(value));
                        break;
                    case ARZEntryType.String:
                        sb.Append(StrTable[value]);
                        break;
                }
                firstentry = false;
            }
            sb.Append(',');
            return sb.ToString();
        }

        /// <summary>
        /// 把本行按【每项包内原始编码】**逐字节**追加到 <paramref name="buf"/>（<b>零转码</b>）。
        /// <para>与 <see cref="ToString"/> 产出同一文本，但绕开 string 中转 —— 一条记录里
        /// 字段名与各字段值**可以各自是不同的编码**（官方包内实测：字段名 ASCII/GBK、值 UTF-8），
        /// 走文本通道只能整体挑一种编码，必然把其中一种写坏（即"解包后变 `涓枃鎻忚堪`"的成因）。</para>
        /// <para>对应 plan §6.2 读侧：按用途判定编码 → 用同一编码落盘。</para>
        /// </summary>
        public void AppendLineBytes(List<byte> buf, Action<string, string> onContentIssue = null)
        {
            ARZStrings st = StrTable;
            if (st == null) return;

            AppendFieldBytes(buf, st[dstrid], st.GetItemEncoding(dstrid), onContentIssue, isKey: true);
            buf.Add((byte)',');
            bool firstentry = true;
            foreach (int value in values)
            {
                if (!firstentry) buf.Add((byte)';');
                switch (dtype)
                {
                    case ARZEntryType.String:
                        AppendFieldBytes(buf, st[value], st.GetItemEncoding(value), onContentIssue);
                        break;
                    case ARZEntryType.Real:
                        AppendFieldBytes(buf, FormatReal(value), Encoding.ASCII);
                        break;
                    default:
                        AppendFieldBytes(buf, value.ToString(), Encoding.ASCII);
                        break;
                }
                firstentry = false;
            }
            buf.Add((byte)',');
        }

        /// <summary>按指定编码把一段文本追加进字节缓冲；顺带修复内嵌换行（与文本通道行为一致）。
        /// 统一内容检测：换行=自动删除；逗号/控制字符=按原样保留、仅通过 <paramref name="onContentIssue"/> 报告。
        /// <paramref name="isKey"/>：字段名段不做内容检测（与检测按钮口径一致，避免误报"值含逗号"）。</summary>
        private void AppendFieldBytes(List<byte> buf, string text, Encoding enc, Action<string, string> onContentIssue = null, bool isKey = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
            {
                ARZStrings st = StrTable;
                if (parent != null && parent.rfid >= 0 && st != null)
                {
                    string recName = st[parent.rfid];
                    Program.Log.Warn(LanguageManager.Instance.GetText("LOG.RecordNewlineFix",
                        "记录 \"{0}\" 的字段 \"{1}\" 值含换行，已删除换行（值可能与相邻内容粘连）"), recName, Name);
                    onContentIssue?.Invoke(DbrContentCheck.KindNewline, recName + " → " + Name);
                }
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\r\n?|\n", "");
            }
            // 逗号 / 控制字符：与检测按钮、CLI --check、打包同一套判定（零分配位掩码），出现即报告、原样保留
            if (!isKey)
            {
                var flags = DbrContentCheck.CheckFlags(text) & ~DbrContentFlags.Newline;   // 换行已在上方处理
                if (flags != DbrContentFlags.None && parent != null && parent.rfid >= 0 && StrTable != null)
                {
                    string recName = StrTable[parent.rfid];
                    // 报告顺序与 Check 一致：逗号 → 控制字符
                    if ((flags & DbrContentFlags.Comma) != 0)
                    {
                        Program.Log.Warn(LanguageManager.Instance.GetText("LOG.RecordCommaWarn",
                            "记录 \"{0}\" 的字段 \"{1}\" 值含逗号，已按原样保留"), recName, Name);
                        onContentIssue?.Invoke(DbrContentCheck.KindComma, recName + " → " + Name);
                    }
                    if ((flags & DbrContentFlags.ControlChar) != 0)
                    {
                        Program.Log.Warn(LanguageManager.Instance.GetText("LOG.RecordControlCharWarn",
                            "记录 \"{0}\" 的字段 \"{1}\" 值含控制字符，已按原样保留"), recName, Name);
                        onContentIssue?.Invoke(DbrContentCheck.KindControlChar, recName + " → " + Name);
                    }
                }
            }
            byte[] bytes = (enc ?? Encoding.UTF8).GetBytes(text);
            buf.AddRange(bytes);
        }
    }

    public class ARZHeader
    {
        public const int HEADER_SIZE = 24;
        public short Unknown = 0x02; // Magick
        public short Version = 0x03;
        public long RecordTableStart;
        public long RecordTableSize;
        public int RecordTableEntries;
        public long StringTableStart;
        public long StringTableSize;
        public ARZHeader()
        {
        }

        public ARZHeader(Stream astream)
        {
            using (BinaryReader br = new BinaryReader(astream, NameCodec.Acp, true))
                ReadBytes(br);
        }

        public void WriteToStream(Stream astream) {
            using (BinaryWriter bw = new BinaryWriter(astream, NameCodec.Acp, true))
            {
                bw.Write(Unknown);
                bw.Write(Version);
                // 官方格式为无符号u32，写入uint（低32位）
                bw.Write((uint)RecordTableStart);
                bw.Write((uint)RecordTableSize);
                bw.Write(RecordTableEntries);
                bw.Write((uint)StringTableStart);
                bw.Write((uint)StringTableSize);
            }
        }

        public void ReadBytes(BinaryReader bytes)
        {
            Unknown = bytes.ReadInt16();
            Version = bytes.ReadInt16();
            // 官方格式为无符号u32（偏移可超过2GB），按uint读取后存入long
            RecordTableStart = bytes.ReadUInt32();
            RecordTableSize = bytes.ReadUInt32();
            RecordTableEntries = bytes.ReadInt32();
            StringTableStart = bytes.ReadUInt32();
            StringTableSize = bytes.ReadUInt32();
        }

    }

}