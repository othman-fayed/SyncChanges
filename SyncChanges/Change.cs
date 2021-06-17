using System.Collections.Generic;
using System.Linq;

namespace SyncChanges
{
    class Change
    {
        public TableInfo Table { get; set; }
        public long Version { get; set; }
        public long CreationVersion { get; set; }

        /// <summary>
        /// I = Insert, U = Update, D = Delete, Z = Repopulate and insert data
        /// </summary>
        public char Operation { get; set; }
        public Dictionary<string, object> Keys { get; private set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Others { get; private set; } = new Dictionary<string, object>();
        public Dictionary<ForeignKeyConstraint, long> ForeignKeyConstraintsToDisable { get; private set; } = new Dictionary<ForeignKeyConstraint, long>();

        public object[] GetValues() => Keys.Values.Concat(Others.Values).ToArray();

        public List<string> GetColumnNames() => Keys.Keys.Concat(Others.Keys).ToList();
        public List<Change> SubChanges { get; set; } = new List<Change>();

        public object GetValue(string columnName)
        {
            if (!Keys.TryGetValue(columnName, out object o) && !Others.TryGetValue(columnName, out o))
                return null;
            return o;
        }

    }
}