using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SyncChanges
{
    /// <summary>
    /// Represents configuration information for the replication of database changes.
    /// </summary>
    public class Config
    {

        /// <summary>
        /// Gets the replication sets.
        /// </summary>
        /// <value>
        /// The replication sets.
        /// </value>
        public List<ReplicationSet> ReplicationSets { get; private set; } = new List<ReplicationSet>();
    }

    /// <summary>
    /// Represents a replication sets, i.e. the combination of a source database and one or more destination databases.
    /// </summary>
    public class ReplicationSet
    {
        /// <summary>
        /// Gets or sets the name of the replication set. This is just for identification in logs etc.
        /// </summary>
        /// <value>
        /// The name of the replication set.
        /// </value>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the source database.
        /// </summary>
        /// <value>
        /// The source database.
        /// </value>
        public DatabaseInfo Source { get; set; }

        /// <summary>
        /// Gets the destination databases.
        /// </summary>
        /// <value>
        /// The destination databases.
        /// </value>
        public List<DatabaseInfo> Destinations { get; private set; } = new List<DatabaseInfo>();

        /// <summary>
        /// Gets or sets the names of the tables to be replicated. If this is empty, all (non-system) tables will be replicated.
        /// </summary>
        /// <value>
        /// The tables to be replicated.
        /// </value>
        public List<string> Tables { get; set; } = new List<string>();

    }

    /// <summary>
    /// Represents information about a database.
    /// </summary>
    public class DatabaseInfo
    {
        /// <summary>
        /// Gets or sets the name of the database. Used solely for identification in logs etc.
        /// </summary>
        /// <value>
        /// The name of the database.
        /// </value>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the connection string.
        /// </summary>
        /// <value>
        /// The connection string.
        /// </value>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Batch size in migration
        /// </summary>
        public int? BatchSize { get; set; }

        /// <summary>
        /// If out of sync db was met, should we populate it?
        /// </summary>
        public bool PopulateOutOfSync { get; set; } = false;

        /// <summary>
        /// Defaults to Slave. Mode of replica db tell us how to treat it
        /// </summary>
        public ReplicaDatabaseMode Mode { get; set; } = ReplicaDatabaseMode.Slave;

        /// <summary>
        /// Table mapping
        /// </summary>
        public IList<TableMapping> TableMapping { get; set; } = new List<TableMapping>();

        /// <summary>
        /// Adds a row version column to be used by SyncChanges
        /// </summary>
        public bool AddRowVersionColumn { get; set; } = true;

        /// <summary>
        /// Row version name
        /// </summary>
        public string RowVersionColumnName { get; set; } = "Sync_RowVersion";
    }

    /// <summary>
    /// Replica database mode
    /// </summary>
    public enum ReplicaDatabaseMode
    {
        /// <summary>
        /// We can't flush the data in the case of live db
        /// </summary>
        Normal,
        /// <summary>
        /// Replica only database
        /// </summary>
        Slave
    }
}
