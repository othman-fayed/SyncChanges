using System.Collections.Generic;

namespace SyncChanges
{
    class TableInfo
    {
        public string Name { get; set; }
        public IList<string> KeyColumns { get; set; }
        public IList<string> OtherColumns { get; set; }
        public IList<ForeignKeyConstraint> ForeignKeyConstraints { get; set; }
        public IList<UniqueConstraint> UniqueConstraints { get; set; }
        public bool HasIdentity { get; set; }
        public bool IsChangeTrackingEnabled { get; set; }
        
        /// <summary>
        /// Ordering used to determine the order in which tables are processed based on their dependencies
        /// </summary>
        public int DependencyOrder { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}