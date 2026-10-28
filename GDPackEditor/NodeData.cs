using System;
using System.Collections.Generic;

namespace GDPackEditor
{
    public class NodeData
    {
        public string Name { get; set; }
        public bool IsDirectory { get; set; }
        public List<NodeData> Children { get; set; } = new List<NodeData>();
        public int TotalItemCount { get; set; }
        public DateTime? FileTime { get; set; }
        public int? RecordIndex { get; set; }
        public string RecordType { get; set; }
        public string State { get; set; } = "";

        public NodeData DeepCopy()
        {
            var copy = new NodeData
            {
                Name = Name,
                IsDirectory = IsDirectory,
                TotalItemCount = TotalItemCount,
                FileTime = FileTime,
                RecordIndex = RecordIndex,
                RecordType = RecordType,
                State = State
            };
            foreach (var child in Children)
                copy.Children.Add(child.DeepCopy());
            return copy;
        }
    }
}
