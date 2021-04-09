using System.Collections.Generic;

namespace SyncChanges
{
    class ChangeInfo
    {
        public long Version { get; set; }
        public List<Change> Changes { get; private set; } = new List<Change>();

        /// <summary>
        /// Tables will flushed before other changes
        /// </summary>
        //public IList<TableInfo> FlushTables { get; set; } = new List<TableInfo>();
    }
}