using System;
using System.Collections.Generic;
using System.Linq;
using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DiffPlex.Model;

namespace GDPackEditor
{
    public enum DiffMatchMode
    {
        Auto,
        Original
    }

    public enum DiffLineSide
    {
        Left,
        Right
    }

    public enum DiffLineType
    {
        Unchanged,
        Deleted,
        Inserted,
        Modified
    }

    public class DiffSubPiece
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public string Text { get; set; }
        public bool IsDifferent { get; set; }
    }

    public class DiffLineInfo
    {
        public DiffLineSide Side { get; set; }
        public string Text { get; set; }
        public DiffLineType Type { get; set; }
        public int OriginalIndex { get; set; }
        public List<DiffSubPiece> SubPieces { get; set; }
    }

    public class FoldingRange
    {
        public int StartLine { get; set; }
        public int LineCount { get; set; }
        public bool IsDifferent { get; set; }
        public string Label { get; set; }
    }

    public class DiffCompareResult
    {
        public List<DiffLineInfo> LeftLines { get; set; }
        public List<DiffLineInfo> RightLines { get; set; }
        public List<FoldingRange> FoldingRanges { get; set; }
        public int DiffCount { get; set; }

        public DiffCompareResult()
        {
            LeftLines = new List<DiffLineInfo>();
            RightLines = new List<DiffLineInfo>();
            FoldingRanges = new List<FoldingRange>();
        }
    }

    public static class TextDiffEngine
    {
        private static readonly IChunker LineChunker = new LineChunker();
        private static readonly IChunker WordChunker = new DelimiterChunker(new[] { ' ', '\t', '=', ',', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\' });

        public static DiffCompareResult ComputeDiff(string text1, string text2, DiffMatchMode mode)
        {
            if (text1 == null) text1 = "";
            if (text2 == null) text2 = "";

            var differ = new Differ();

            if (mode == DiffMatchMode.Original)
                return ComputeOriginalDiff(differ, text1, text2);
            else
                return ComputeAutoDiff(differ, text1, text2);
        }

        private static DiffCompareResult ComputeOriginalDiff(Differ differ, string text1, string text2)
        {
            var result = new DiffCompareResult();
            var diffResult = differ.CreateDiffs(text1, text2, false, false, LineChunker);

            int leftIdx = 0;
            int rightIdx = 0;

            foreach (var block in diffResult.DiffBlocks)
            {
                for (int i = 0; i < block.DeleteCountA; i++)
                {
                    string line = diffResult.PiecesOld[block.DeleteStartA + i];
                    result.LeftLines.Add(new DiffLineInfo
                    {
                        Side = DiffLineSide.Left,
                        Text = line,
                        Type = DiffLineType.Deleted,
                        OriginalIndex = leftIdx++
                    });
                    result.RightLines.Add(new DiffLineInfo
                    {
                        Side = DiffLineSide.Right,
                        Text = "",
                        Type = DiffLineType.Inserted,
                        OriginalIndex = -1
                    });
                }
                for (int i = 0; i < block.InsertCountB; i++)
                {
                    string line = diffResult.PiecesNew[block.InsertStartB + i];
                    result.LeftLines.Add(new DiffLineInfo
                    {
                        Side = DiffLineSide.Left,
                        Text = "",
                        Type = DiffLineType.Inserted,
                        OriginalIndex = -1
                    });
                    result.RightLines.Add(new DiffLineInfo
                    {
                        Side = DiffLineSide.Right,
                        Text = line,
                        Type = DiffLineType.Inserted,
                        OriginalIndex = rightIdx++
                    });
                }
            }

            result.DiffCount = result.LeftLines.Count(l => l.Type != DiffLineType.Unchanged);
            GenerateFoldingRanges(result);
            return result;
        }

        private static DiffCompareResult ComputeAutoDiff(Differ differ, string text1, string text2)
        {
            // 语义 A：红=仅左(删除)、绿=仅右(新增)、黄=两边都有但内容不同(修改)。使用原始行级 LCS 直接遍历 DiffBlocks，
            // 将 DeleteCountA/InsertCountB 的配对部分标为 Modified(黄)，剩余标为 Deleted(红)/Inserted(绿)，并保持编辑器对齐(不补空行)。
            var result = new DiffCompareResult();
            var diffResult = differ.CreateDiffs(text1, text2, false, false, LineChunker);
            int aPos = 0;
            int bPos = 0;
            var modifiedPairs = new List<(DiffLineInfo left, DiffLineInfo right)>();
            foreach (var block in diffResult.DiffBlocks)
            {
                while (aPos < block.DeleteStartA && bPos < block.InsertStartB)
                {
                    string oldLine = diffResult.PiecesOld[aPos];
                    string newLine = diffResult.PiecesNew[bPos];
                    result.LeftLines.Add(new DiffLineInfo { Side = DiffLineSide.Left, Text = oldLine, Type = DiffLineType.Unchanged, OriginalIndex = result.LeftLines.Count });
                    result.RightLines.Add(new DiffLineInfo { Side = DiffLineSide.Right, Text = newLine, Type = DiffLineType.Unchanged, OriginalIndex = result.RightLines.Count });
                    aPos++; bPos++;
                }
                int min = Math.Min(block.DeleteCountA, block.InsertCountB);
                for (int i = 0; i < min; i++)
                {
                    string oldLine = diffResult.PiecesOld[block.DeleteStartA + i];
                    string newLine = diffResult.PiecesNew[block.InsertStartB + i];
                    var leftInfo = new DiffLineInfo { Side = DiffLineSide.Left, Text = oldLine, Type = DiffLineType.Modified, OriginalIndex = result.LeftLines.Count };
                    var rightInfo = new DiffLineInfo { Side = DiffLineSide.Right, Text = newLine, Type = DiffLineType.Modified, OriginalIndex = result.RightLines.Count };
                    result.LeftLines.Add(leftInfo);
                    result.RightLines.Add(rightInfo);
                    modifiedPairs.Add((leftInfo, rightInfo));
                }
                for (int i = min; i < block.DeleteCountA; i++)
                {
                    string oldLine = diffResult.PiecesOld[block.DeleteStartA + i];
                    result.LeftLines.Add(new DiffLineInfo { Side = DiffLineSide.Left, Text = oldLine, Type = DiffLineType.Deleted, OriginalIndex = result.LeftLines.Count });
                }
                for (int i = min; i < block.InsertCountB; i++)
                {
                    string newLine = diffResult.PiecesNew[block.InsertStartB + i];
                    result.RightLines.Add(new DiffLineInfo { Side = DiffLineSide.Right, Text = newLine, Type = DiffLineType.Inserted, OriginalIndex = result.RightLines.Count });
                }
                aPos = block.DeleteStartA + block.DeleteCountA;
                bPos = block.InsertStartB + block.InsertCountB;
            }
            while (aPos < diffResult.PiecesOld.Count && bPos < diffResult.PiecesNew.Count)
            {
                string oldLine = diffResult.PiecesOld[aPos];
                string newLine = diffResult.PiecesNew[bPos];
                result.LeftLines.Add(new DiffLineInfo { Side = DiffLineSide.Left, Text = oldLine, Type = DiffLineType.Unchanged, OriginalIndex = result.LeftLines.Count });
                result.RightLines.Add(new DiffLineInfo { Side = DiffLineSide.Right, Text = newLine, Type = DiffLineType.Unchanged, OriginalIndex = result.RightLines.Count });
                aPos++; bPos++;
            }
            while (aPos < diffResult.PiecesOld.Count)
            {
                string oldLine = diffResult.PiecesOld[aPos];
                result.LeftLines.Add(new DiffLineInfo { Side = DiffLineSide.Left, Text = oldLine, Type = DiffLineType.Deleted, OriginalIndex = result.LeftLines.Count });
                aPos++;
            }
            while (bPos < diffResult.PiecesNew.Count)
            {
                string newLine = diffResult.PiecesNew[bPos];
                result.RightLines.Add(new DiffLineInfo { Side = DiffLineSide.Right, Text = newLine, Type = DiffLineType.Inserted, OriginalIndex = result.RightLines.Count });
                bPos++;
            }
            foreach (var (left, right) in modifiedPairs)
            {
                var wordDiff = differ.CreateDiffs(left.Text, right.Text, false, false, WordChunker);
                left.SubPieces = BuildSubPieces(left.Text, wordDiff.PiecesOld, wordDiff.DiffBlocks, true);
                right.SubPieces = BuildSubPieces(right.Text, wordDiff.PiecesNew, wordDiff.DiffBlocks, false);
            }
            // 修改计数：左侧非未变 + 右侧纯新增(已去重，避免 Modified 重复计数)
            result.DiffCount = result.LeftLines.Count(l => l.Type != DiffLineType.Unchanged) + result.RightLines.Count(r => r.Type == DiffLineType.Inserted);
            GenerateFoldingRanges(result);
            return result;
        }

        private static void ComputeWordLevelPieces(Differ differ, DiffCompareResult result)
        {
            int maxLen = Math.Max(result.LeftLines.Count, result.RightLines.Count);
            for (int i = 0; i < maxLen; i++)
            {
                if (i >= result.LeftLines.Count || i >= result.RightLines.Count) break;

                var left = result.LeftLines[i];
                var right = result.RightLines[i];

                if (left.Type != DiffLineType.Modified) continue;

                var wordDiff = differ.CreateDiffs(left.Text, right.Text, false, false, WordChunker);

                left.SubPieces = BuildSubPieces(left.Text, wordDiff.PiecesOld, wordDiff.DiffBlocks, true);
                right.SubPieces = BuildSubPieces(right.Text, wordDiff.PiecesNew, wordDiff.DiffBlocks, false);
            }
        }

        private static List<DiffSubPiece> BuildSubPieces(string fullText, IReadOnlyList<string> pieces, IList<DiffBlock> blocks, bool isOld)
        {
            var subPieces = new List<DiffSubPiece>();
            if (string.IsNullOrEmpty(fullText) || pieces.Count == 0)
            {
                if (!string.IsNullOrEmpty(fullText))
                    subPieces.Add(new DiffSubPiece { Start = 0, Length = fullText.Length, Text = fullText, IsDifferent = false });
                return subPieces;
            }

            var pieceStatus = new bool[pieces.Count];

            foreach (var block in blocks)
            {
                int start = isOld ? block.DeleteStartA : block.InsertStartB;
                int count = isOld ? block.DeleteCountA : block.InsertCountB;
                for (int i = start; i < start + count && i < pieces.Count; i++)
                {
                    pieceStatus[i] = true;
                }
            }

            int pos = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                string piece = pieces[i];
                if (pos < fullText.Length)
                {
                    int len = Math.Min(piece.Length, fullText.Length - pos);
                    subPieces.Add(new DiffSubPiece
                    {
                        Start = pos,
                        Length = len,
                        Text = fullText.Substring(pos, len),
                        IsDifferent = pieceStatus[i]
                    });
                    pos += len;
                }
            }

            if (pos < fullText.Length)
            {
                subPieces.Add(new DiffSubPiece
                {
                    Start = pos,
                    Length = fullText.Length - pos,
                    Text = fullText.Substring(pos),
                    IsDifferent = false
                });
            }

            return subPieces;
        }

        private static void GenerateFoldingRanges(DiffCompareResult result)
        {
            int count = Math.Min(result.LeftLines.Count, result.RightLines.Count);
            int rangeStart = -1;

            for (int i = 0; i <= count; i++)
            {
                bool isUnchanged = i < count &&
                    result.LeftLines[i].Type == DiffLineType.Unchanged &&
                    result.RightLines[i].Type == DiffLineType.Unchanged;

                if (isUnchanged)
                {
                    if (rangeStart == -1) rangeStart = i;
                }
                else
                {
                    if (rangeStart != -1)
                    {
                        int lineCount = i - rangeStart;
                        if (lineCount >= 3)
                        {
                            result.FoldingRanges.Add(new FoldingRange
                            {
                                StartLine = rangeStart,
                                LineCount = lineCount,
                                IsDifferent = false,
                                Label = string.Format("已折叠 {0} 行", lineCount)
                            });
                        }
                        rangeStart = -1;
                    }
                }
            }
        }

        public static int FindNextDiff(List<DiffLineInfo> lines, int currentLine, bool forward)
        {
            if (lines == null || lines.Count == 0) return -1;

            int start = forward ? currentLine + 1 : currentLine - 1;
            int step = forward ? 1 : -1;

            for (int i = start; i >= 0 && i < lines.Count; i += step)
            {
                if (lines[i].Type != DiffLineType.Unchanged)
                    return i;
            }
            return -1;
        }
    }
}
