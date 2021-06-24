using System.Collections.Generic;
using System.Data.Common;

namespace SyncChanges
{
    /// <summary>
    /// Defines table mapping settings
    /// </summary>
    public class TableMapping
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public IList<ColumnMapping> ColumnMappings { get; set; } = new List<ColumnMapping>();
    }

    public class ColumnMapping
    {
        public string Source { get; set; }
        public string Target { get; set; }
    }
}