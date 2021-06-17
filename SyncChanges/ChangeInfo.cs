using System.Collections.Generic;

namespace SyncChanges
{
    class ChangeInfo
    {
        public long Version { get; set; }
        public List<Change> Changes { get; private set; } = new List<Change>();
        
        /// <summary>
        /// Contains a list of out of sync versions
        /// </summary>
        public HashSet<long> OutOfSyncVersions { get; set; } = new HashSet<long>();
        public HashSet<DatabaseInfo> OutOfSyncDatabases { get; set; } = new HashSet<DatabaseInfo>();
    }
}