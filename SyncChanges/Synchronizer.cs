﻿using Humanizer;
using NLog;
using NPoco;
using NPoco.fastJSON;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Resources;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace SyncChanges
{
    /// <summary>
    /// Allows replication of database changes from a source database to one or more destination databases.
    /// </summary>
    public class Synchronizer
    {
        /// <summary>
        /// Gets or sets a value indicating whether destination databases will be modified during a replication run.
        /// </summary>
        /// <value>
        ///   <c>true</c> if destination databases will be modified; otherwise, <c>false</c>.
        /// </value>
        public bool DryRun { get; set; } = false;

        /// <summary>
        /// Gets or sets the database connection timeout.
        /// </summary>
        /// <value>
        /// The database connection timeout.
        /// </value>
        public int Timeout { get; set; } = 0;

        /// <summary>
        /// Gets or sets the minimum synchronization time interval in seconds. Default is 30 seconds.
        /// </summary>
        /// <value>
        /// The synchronization time interval in seconds.
        /// </value>
        public int Interval { get; set; } = 30;

        /// <summary>
        /// Occurs when synchronization from a synchronization loop has succeeded.
        /// </summary>
        public event EventHandler<SyncEventArgs> Synced;

        public SyncSession CurrentSession { get; private set; }

        static readonly Logger Log = LogManager.GetCurrentClassLogger();
        Config Config { get; set; }
        bool Error { get; set; }

        /// <summary>
        /// Use the Version set in SyncInfo table instead of getting the min version per table
        /// </summary>
        public bool UseReplicaDatabaseVersionInsteadOfPerTable { get; private set; }
        public bool IgnoreDuplicateKeyInserts { get; private set; }

        public bool AllowRepopulate { get; private set; }
        /// <summary>
        /// Initializes a new instance of the <see cref="Synchronizer"/> class.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <exception cref="System.ArgumentException"><paramref name="config"/> is null</exception>
        public Synchronizer(Config config)
        {
            Config = config ?? throw new ArgumentException("config is null", nameof(config));
        }

        private IList<IList<TableInfo>> Tables { get; } = new List<IList<TableInfo>>();

        private bool Initialized = false;

        /// <summary>
        /// Initialize the synchronization process.
        /// </summary>
        public void Init()
        {
            if (Timeout != 0)
            {
                Log.Info($"Command timeout is {"second".ToQuantity(Timeout)}");
            }

            for (int i = 0; i < Config.ReplicationSets.Count; i++)
            {
                var replicationSet = Config.ReplicationSets[i];

                Log.Info($"Getting replication information for replication set {replicationSet.Name}");

                var tables = GetTables(replicationSet.Source, replicationSet.ExcludeTables);

                // Force adding tables.. Should we !?
                if (replicationSet.Tables != null && replicationSet.Tables.Any())
                {
                    tables = tables
                        .Select(t => new { Table = t, Name = t.Name.Replace("[", "").Replace("]", "") })
                        .Where(t => replicationSet.Tables.Any(r => r == t.Name || r == t.Name.Split('.')[1]))
                        .Select(t => t.Table)
                        .ToList();
                }

                if (!tables.Any())
                {
                    Log.Warn("No tables to replicate (check if change tracking is enabled)");
                }
                else
                {
                    Log.Info($"Replicating {"table".ToQuantity(tables.Count, ShowQuantityAs.None)} {string.Join(", ", tables.Select(t => t.Name))}");
                }

                Tables.Add(tables);
            }

            this.CurrentSession = SyncSession.LoadSessionFromFile();

            Initialized = true;
        }

        /// <summary>
        /// Perform the synchronization.
        /// </summary>
        /// <returns>true, if the synchronization was successful; otherwise, false.</returns>
        public bool Sync()
        {
            Error = false;

            if (!Initialized) Init();

            var startIndex = 0;
            if (CurrentSession.InProgress == true)
            {
                startIndex = Config.ReplicationSets.FindIndex(x => x.Name == CurrentSession.DestinationName);
                if (startIndex == -1)
                {
                    startIndex = 0;
                }
            }

            for (int i = startIndex; i < Config.ReplicationSets.Count; i++)
            {
                var replicationSet = Config.ReplicationSets[i];
                var tables = Tables[i];
                CurrentSession.DestinationName = replicationSet.Name;

                Sync(replicationSet, tables);
            }

            CurrentSession = new SyncSession();

            Log.Info($"Finished replication {(Error ? "with" : "without")} errors");

            return !Error;
        }

        private bool Sync(ReplicationSet replicationSet, IList<TableInfo> tables, long sourceVersion = -1)
        {
            Error = false;

            if (!tables.Any()) return true;

            Log.Info($"Starting replication for replication set {replicationSet.Name}");

            var destinationsByVersionGroup = replicationSet
                .Destinations
                .GroupBy(d => GetCurrentVersion(d));

            var destinationsByVersion = destinationsByVersionGroup
                .Where(d => d.Key >= 0 && (sourceVersion < 0 || d.Key < sourceVersion))
                .ToList();

            foreach (var destinations in destinationsByVersion)
            {
                Replicate(replicationSet.Source, destinations, tables);
            }

            return !Error;
        }

        /// <summary>
        /// Performs synchronization in an infinite loop. Periodically checks if source version has increased to trigger replication.
        /// </summary>
        /// <param name="token">The cancellation token.</param>
        public void SyncLoop(CancellationToken token)
        {
            var currentVersions = Enumerable.Repeat(0L, Config.ReplicationSets.Count).ToList();

            if (!Initialized) Init();

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    Log.Info("Stopping replication.");
                    return;
                }

                var start = DateTime.UtcNow;
                Error = false;

                for (int i = 0; i < Config.ReplicationSets.Count; i++)
                {
                    var replicationSet = Config.ReplicationSets[i];
                    var currentVersion = currentVersions[i];
                    long version = 0;

                    try
                    {
                        // TODO: Add code to handle if Change Tracking is not enabled
                        // It's being handled in GetCurrentVersion method
                        /*
                         * Option 1: 
                         * SELECT * FROM sys.change_tracking_databases WHERE database_id = DB_ID('model');
                         * Returns no rows if change tracking is not enabled // Needs verification
                         * 
                         * Option 2: 
                         * select CHANGE_TRACKING_CURRENT_VERSION()
                         * Returns null     // Needs verification
                         */

                        version = GetCurrentVersion(replicationSet.Source); // Returns -1 if tracking not enabled

                        if (version == -1)
                        {
                            Log.Warn($"Change tracking is not enabled for source database {replicationSet.Source.Name}");
                            Error = true;
                            continue;
                        }

                        //using (var db = GetDatabase(replicationSet.Source.ConnectionString, DatabaseType.SqlServer2008))
                        //{
                        //    version = db.ExecuteScalar<long>("select CHANGE_TRACKING_CURRENT_VERSION()");
                        //}

                        Log.Debug($"Current version of source in replication set {replicationSet.Name} is {version}.");

                        if (version > currentVersion)
                        {
                            Log.Info($"Current version of source in replication set {replicationSet.Name} has increased from {currentVersion} to {version}: Starting replication.");

                            var tables = Tables[i];
                            var success = Sync(replicationSet, tables, version);

                            if (success) currentVersions[i] = version;

                            Synced?.Invoke(this, new SyncEventArgs { ReplicationSet = replicationSet, Version = version });
                        }
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Error occurred during replication of set {replicationSet.Name}.");
                        Error = true;
                    }
#pragma warning restore CA1031 // Do not catch general exception types

                    if (token.IsCancellationRequested)
                    {
                        Log.Info("Stopping replication.");
                        return;
                    }
                }

                Log.Info($"Finished replication {(Error ? "with" : "without")} errors");

                var delay = (int)Math.Round(Math.Max(0, (TimeSpan.FromSeconds(Interval) - (DateTime.UtcNow - start)).TotalSeconds) * 1000, MidpointRounding.AwayFromZero);
                Thread.Sleep(delay);
            }
        }

        private IList<TableInfo> GetTables(DatabaseInfo dbInfo, List<string> excludeTables = null)
        {
            try
            {
                using var db = GetDatabase(dbInfo.ConnectionString, DatabaseType.SqlServer2008);

                //var sql = @"select TableName, ColumnName, coalesce(max(cast(is_primary_key as tinyint)), 0) PrimaryKey from
                //var sql = @"select TableName, ColumnName, iif(max(cast(is_primary_key as tinyint)) = 1, 1, 0) PrimaryKey, iif(max(cast(is_identity as tinyint)) = 1, 1, 0) IdentityKey from
                //var sql = @"select TableName, ColumnName, coalesce(max(cast(is_primary_key as tinyint)), 0) PrimaryKey,
                // coalesce(max(cast(is_identity as tinyint)), 0) IsIdentity from
                var sql = @"select 
    TableName, 
    ColumnName, 
    coalesce(max(cast(is_primary_key as tinyint)), 0) PrimaryKey, 
    coalesce(max(cast(is_identity as tinyint)), 0) IdentityKey,
    MinValidVersion 
    from
    (
        select ('[' + s.name + '].[' + t.name + ']') TableName, ('[' + COL_NAME(t.object_id, a.column_id) + ']') ColumnName,
        i.is_primary_key, a.is_identity, tr.min_valid_version MinValidVersion
        from sys.change_tracking_tables tr
        right join sys.tables t on t.object_id = tr.object_id
        join sys.schemas s on s.schema_id = t.schema_id
        join sys.columns a on a.object_id = t.object_id
        JOIN sys.types ct ON ct.system_type_id = a.system_type_id
        left join sys.index_columns c on c.object_id = t.object_id and c.column_id = a.column_id
        left join sys.indexes i on i.object_id = t.object_id and i.index_id = c.index_id
        where a.is_computed = 0 and ct.name != 'timestamp' and t.name != 'MSchange_tracking_history'
    ) X
    group by TableName, ColumnName, MinValidVersion
    order by TableName, ColumnName
";

                var tables = db.Fetch<dynamic>(sql)
                    .GroupBy(t => t.TableName)
                    .Select(g => new TableInfo
                    {
                        Name = (string)g.Key,
                        KeyColumns = g.Where(c => (int)c.PrimaryKey > 0).Select(c => (string)c.ColumnName).ToList(),
                        OtherColumns = g.Where(c => (int)c.PrimaryKey == 0).Select(c => (string)c.ColumnName).ToList(),
                        HasIdentity = g.Any(c => (int)c.IdentityKey > 0),
                        IsChangeTrackingEnabled = g.First().MinValidVersion != null,
                    }).ToList();

                if (excludeTables?.Any() == true)
                {
                    tables = tables
                        .Select(t => new { Table = t, Name = t.Name.Replace("[", "").Replace("]", "") })
                        .Where(t => false == excludeTables.Any(r => r == t.Name || r == t.Name.Split('.')[1]))
                        .Select(t => t.Table)
                        .ToList();
                }

                var untrackedTables = tables.Where(x => x.IsChangeTrackingEnabled == false).ToList();

                if (untrackedTables.Any())
                {
                    Log.Fatal($"Untracked tables are: {string.Join(", ", untrackedTables.Select(x => x.Name))}");
                    Log.Info(@"Use the following query to enable change tracking on table: 'ALTER TABLE Person.Contact  
ENABLE CHANGE_TRACKING
WITH(TRACK_COLUMNS_UPDATED = OFF)'  ");
                    untrackedTables.ForEach(t => Log.Debug($"ALTER TABLE {t.Name} ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF)"));
                    throw new Exception("Untracked tables found");
                }

                var fks = db.Fetch<ForeignKeyConstraint>(@"select obj.name AS ForeignKeyName,
                            ('[' + sch.name + '].[' + tab1.name + ']') TableName,
                            ('[' +  col1.name + ']') ColumnName,
                            ('[' + sch2.name + '].[' + tab2.name + ']') ReferencedTableName,
                            ('[' +  col2.name + ']') ReferencedColumnName
                        from sys.foreign_key_columns fkc
                        inner join sys.foreign_keys obj
                            on obj.object_id = fkc.constraint_object_id
                        inner join sys.tables tab1
                            on tab1.object_id = fkc.parent_object_id
                        inner join sys.schemas sch
                            on tab1.schema_id = sch.schema_id
                        inner join sys.columns col1
                            on col1.column_id = parent_column_id AND col1.object_id = tab1.object_id
                        inner join sys.tables tab2
                            on tab2.object_id = fkc.referenced_object_id
                        inner join sys.schemas sch2
                            on tab2.schema_id = sch2.schema_id
                        inner join sys.columns col2
                            on col2.column_id = referenced_column_id AND col2.object_id = tab2.object_id
                        where obj.is_disabled = 0");

                foreach (var table in tables)
                    table.ForeignKeyConstraints = fks.Where(f => f.TableName == table.Name).ToList();

                var uqcs = db.Fetch<UniqueColumn>(@"SELECT
                             IndexId = ('[' + sch.name + '].[' + t.name + '].[' + ind.name + ']'),
                             TableName = ('[' + sch.name + '].[' + t.name + ']'),
                             IndexName = ind.name,
                             ColumnName = ('[' +  col.name + ']'),
                             IsConstraint = is_unique_constraint
                        FROM
                             sys.indexes ind
                        INNER JOIN
                             sys.index_columns ic ON  ind.object_id = ic.object_id and ind.index_id = ic.index_id
                        INNER JOIN
                             sys.columns col ON ic.object_id = col.object_id and ic.column_id = col.column_id
                        INNER JOIN
                             sys.tables t ON ind.object_id = t.object_id
                        inner join sys.schemas sch
                            on t.schema_id = sch.schema_id
                        WHERE
                             ind.is_primary_key = 0
                             AND (ind.is_unique = 1 or ind.is_unique_constraint = 1)
                             AND t.is_ms_shipped = 0
                        ORDER BY
                             t.name, ind.name, ind.index_id, ic.is_included_column, ic.key_ordinal;");

                var uqs = uqcs.GroupBy(c => $"{c.TableName}_{c.IndexName}").Select(g => new UniqueConstraint
                {
                    IndexName = g.First().IndexName,
                    TableName = g.First().TableName,
                    IsConstraint = g.First().IsConstraint,
                    ColumnNames = g.Select(c => c.ColumnName).ToList(),
                }).ToList();


                var sortedTables = new List<TableInfo>();
                foreach (var table in tables)
                {
                    table.UniqueConstraints = uqs.Where(u => u.TableName == table.Name).ToList();

                    var index = sortedTables.FindIndex(x => x.ForeignKeyConstraints.Any(x => x.ReferencedTableName == table.Name));
                    if (index == -1)
                    {
                        sortedTables.Add(table);
                    }
                    else
                    {
                        sortedTables.Insert(index, table);
                    }
                }

                for (int i = 0; i < sortedTables.Count; i++)
                {
                    sortedTables[i].DependencyOrder = i;
                }

                // tables are sorted based on their dependencies
                return sortedTables;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Error getting tables to replicate from source database");
                throw;
            }
        }

        private Database GetDatabase(string connectionString, DatabaseType databaseType = null)
        {
            var db = new Database(connectionString, databaseType ?? DatabaseType.SqlServer2005, System.Data.SqlClient.SqlClientFactory.Instance);

            if (Timeout != 0) db.CommandTimeout = Timeout;

            return db;
        }

        private void Replicate(DatabaseInfo source, IGrouping<long, DatabaseInfo> destinations, IList<TableInfo> tables)
        {
        RetrieveChangesL:
            var changeInfo = RetrieveChanges(source, destinations, tables);
            if (changeInfo == null) return;

            if (changeInfo.OutOfSyncDatabases.Any())
            {
                // We shall loop the out of sync databases and populate per table
                // Loop over table to populate databases.

                changeInfo.Changes.Clear();
                changeInfo.Changes.AddRange(tables.Select(table => new Change { Operation = 'Z', Table = table }));
            }

            // replicate changes to destinations
            var databases = destinations.ToList();

            for (var i = 0; i < databases.Count; i++)
            {
                var destination = databases[i];
                try
                {
                    ReplicateChanges(changeInfo, destination, source);
                }
                catch (Exception exception)
                {
                    Error = true;
                    Log.Error(exception, $"Error replicating changes to destination {destination.Name}");

                    // foreign key error
                    if (exception is SqlException sqlException)
                    {
                        if (sqlException.Number == 547)
                        {
                            // First, we try to use database version to see if some changes were missed
                            if (UseReplicaDatabaseVersionInsteadOfPerTable == false)
                            {
                                UseReplicaDatabaseVersionInsteadOfPerTable = true;
                                IgnoreDuplicateKeyInserts = true;
                                goto RetrieveChangesL;
                            }

                            // Second, we try to disable all constraints. This work if we are inserting in the wrong order
                            if (destination.IsTemporaryDisableAllConstraints())
                            {
                                destination.TemporaryDisableAllConstraints(false);
                            }
                            else if (destination.DisableAllConstraints != true)
                            {
                                Log.Info("Enable DisableAllConstraints on destination {0} due to foreign key error", destination.Name);
                                destination.TemporaryDisableAllConstraints();
                                i--;
                            }
                        }
                    }
                }
            }

            UseReplicaDatabaseVersionInsteadOfPerTable = false;
            IgnoreDuplicateKeyInserts = false;
        }

        private void ReplicateChanges(ChangeInfo changeInfo, DatabaseInfo destination, DatabaseInfo source)
        {
            Log.Info($"Replicating {"change".ToQuantity(changeInfo.Changes.Count)} to destination {destination.Name}");

            using var db = GetDatabase(destination.ConnectionString, DatabaseType.SqlServer2005);
            using var transaction = db.GetTransaction(System.Data.IsolationLevel.ReadUncommitted);

            // Another try is used here assumingly to Dispose correctly
            //try
            //{
            var flushChanges = changeInfo.Changes.Where(x => x.Operation == 'Z').ToList();

            if (flushChanges.Any())
            {
                Log.Debug($"Flushing changes for tables: {string.Join(",", flushChanges.Select(x => x.Table))})");

                DisableAllConstraints(db);

                long changeTrackingVersion = 0;
                foreach (var flushChange in flushChanges)
                {
                    FlushTable(db, flushChange, ref changeTrackingVersion, source: source);
                }

                EnableAllConstraints(db);
            }

            var changes = changeInfo.Changes.Except(flushChanges).ToList();
            var disabledForeignKeyConstraints = new Dictionary<ForeignKeyConstraint, long>();

            if (destination.DisableAllConstraints == true)
            {
                DisableAllConstraints(db);
            }

            for (int i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                Log.Debug($"Replicating change #{i + 1} of {changes.Count} (Version {change.Version}, CreationVersion {change.CreationVersion}, Operation {change.Operation})");

                if (destination.DisableAllConstraints != true)
                {
                    foreach (var fk in change.ForeignKeyConstraintsToDisable)
                    {
                        if (disabledForeignKeyConstraints.TryGetValue(fk.Key, out long untilVersion))
                        {
                            // FK is already disabled, check if it needs to be deferred further than currently planned
                            if (fk.Value > untilVersion)
                                disabledForeignKeyConstraints[fk.Key] = fk.Value;
                        }
                        else
                        {
                            DisableForeignKeyConstraint(db, fk.Key);
                            disabledForeignKeyConstraints[fk.Key] = fk.Value;
                        }
                    }
                }

                PerformChange(db, change);

                if ((i + 1) >= changes.Count || changes[i + 1].CreationVersion > change.CreationVersion) // there may be more than one change with the same CreationVersion
                {
                    foreach (var fk in disabledForeignKeyConstraints.Where(f => f.Value <= change.CreationVersion).Select(f => f.Key).ToList())
                    {
                        ReenableForeignKeyConstraint(db, fk);
                        disabledForeignKeyConstraints.Remove(fk);
                    }
                }
            }

            if (destination.DisableAllConstraints == true)
            {
                EnableAllConstraints(db);
            }
            else if (disabledForeignKeyConstraints.Any())
            {
                Log.Debug($"Re-enabling all disabled foreign keys");
                foreach (var fk in disabledForeignKeyConstraints.Keys.ToList())
                {
                    ReenableForeignKeyConstraint(db, fk);
                    disabledForeignKeyConstraints.Remove(fk);
                }
            }

            if (!DryRun)
            {
                SetSyncVersion(db, changeInfo.Version);
                transaction.Complete();
            }

            Log.Info($"Destination {destination.Name} now at version {changeInfo.Version}");

            // reset DisableAllConstraints after successful run
            if (destination.IsTemporaryDisableAllConstraints())
            {
                Log.Info("Reset DisableAllConstraints on destination {0} ", destination.Name);
                destination.TemporaryDisableAllConstraints(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
        }
#pragma warning restore CA1031 // Do not catch general exception types
        //    }
        //#pragma warning disable CA1031 // Do not catch general exception types
        //                catch (Exception ex)
        //                {
        //                    ExceptionCaught(ex);
        //}
        //#pragma warning restore CA1031 // Do not catch general exception types
        //            }
        //        }

        private void ReenableForeignKeyConstraint(Database db, ForeignKeyConstraint fk)
        {
            Log.Debug($"Re-enabling foreign key constraint {fk.ForeignKeyName}");
            var sql = $"alter table {fk.TableName} with check check constraint [{fk.ForeignKeyName}]";
            if (!DryRun)
            {
                db.Execute(sql);
            }
        }

        private void DisableAllConstraints(Database db)
        {
            //--Disable all constraints for database
            //EXEC sp_msforeachtable "ALTER TABLE ? NOCHECK CONSTRAINT all"
            Log.Debug("Disabling all constraints for database");
            var sql = "EXEC sp_msforeachtable \"ALTER TABLE ? NOCHECK CONSTRAINT all\"";
            if (!DryRun)
            {
                db.Execute(sql);
            }
        }
        private void EnableAllConstraints(Database db)
        {
            //-- Enable all constraints for database
            //EXEC sp_msforeachtable "ALTER TABLE ? WITH CHECK CHECK CONSTRAINT all"

            Log.Debug($"Enable all constraints for database");
            var sql = "EXEC sp_msforeachtable \"ALTER TABLE ? WITH CHECK CHECK CONSTRAINT all\"";
            if (!DryRun)
            {
                db.Execute(sql);
            }
        }

        private void DisableForeignKeyConstraint(Database db, ForeignKeyConstraint fk)
        {
            Log.Debug($"Disabling foreign key constraint {fk.ForeignKeyName}");
            var sql = $"alter table {fk.TableName} nocheck constraint [{fk.ForeignKeyName}]";
            if (!DryRun)
            {
                db.Execute(sql);
            }
        }

        private void SetSyncVersion(Database db, long currentVersion)
        {
            if (!DryRun)
            {
                var syncInfoTableExists = db.ExecuteScalar<string>("select top(1) name from sys.tables where name ='SyncInfo'") != null;

                if (!syncInfoTableExists)
                {
                    db.Execute("create table SyncInfo (Id int not null primary key default 1 check (Id = 1), Version bigint not null)");
                    db.Execute("insert into SyncInfo (Version) values (@0)", currentVersion);
                }
                else
                {
                    db.Execute("update SyncInfo set Version = @0", currentVersion);
                }
            }
        }

        private ChangeInfo RetrieveChanges(
            DatabaseInfo source,
            IGrouping<long, DatabaseInfo> destinations,
            IList<TableInfo> tables,
            int maxChangetrackingVersion = 0)
        {
            var destinationVersion = destinations.Key;
            var changeInfo = new ChangeInfo();
            var changes = new List<Change>();

            using (var db = GetDatabase(source.ConnectionString, DatabaseType.SqlServer2008))
            {
                var snapshotIsolationEnabled = db.ExecuteScalar<int>("select snapshot_isolation_state from sys.databases where name = DB_NAME()") == 1;
                if (snapshotIsolationEnabled)
                {
                    Log.Info($"Snapshot isolation is enabled in database {source.Name}");
                    db.BeginTransaction(System.Data.IsolationLevel.Snapshot);
                }
                else
                {
                    Log.Info($"Snapshot isolation is not enabled in database {source.Name}, ignoring all changes above current version");
                }

                changeInfo.Version = db.ExecuteScalar<long>("select CHANGE_TRACKING_CURRENT_VERSION()");
                Log.Info($"Current version of database {source.Name} is {changeInfo.Version}");

                var debugTables = source.DebugTables != null && source.DebugTables.Any();

                foreach (var table in tables)
                {
                    var tableName = table.Name;
                    var minVersion = UseReplicaDatabaseVersionInsteadOfPerTable
                        ? destinationVersion
                        : db.ExecuteScalar<long?>("select CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@0))", tableName)
                        ;

                    Log.Trace(() => $"select CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID({tableName}))");

                    Log.Info($"Minimum version of table {tableName} in database {source.Name} is {minVersion}");

                    if (minVersion > destinationVersion)
                    {
                        changeInfo.OutOfSyncVersions.Add(destinationVersion);

                        foreach (var databseInfo in destinations)
                        {
                            if (databseInfo.PopulateOutOfSync)
                            {
                                changeInfo.OutOfSyncDatabases.Add(databseInfo);
                            }
                        }

                        if (changeInfo.OutOfSyncDatabases.Any())
                        {
                            return changeInfo;
                        }

                        Log.Error($"Cannot replicate table {tableName} to {"destination".ToQuantity(destinations.Count(), ShowQuantityAs.None)} {string.Join(", ", destinations.Select(d => d.Name))} because minimum source version {minVersion} is greater than destination version {destinationVersion}");
                        Error = true;
                        return null;
                    }
                    //else if (minVersion > destinationVersion && AllowRepopulate)
                    //{
                    //    changeInfo.OutOfSyncVersions.Add(destinationVersion);

                    //    //// Add table to flush list since the table contains data
                    //    //var flushChange = new Change
                    //    //{
                    //    //    Operation = 'Z',                    // Repopulate
                    //    //    CreationVersion = int.MaxValue,     // Disable forign keys
                    //    //    Table = table
                    //    //};
                    //    //changes.Add(flushChange);
                    //    //Log.Info($"Table {tableName} has to be repopulated");

                    //    //flushChange.SubChanges = GetAllData(db, table, changeInfo.Version);
                    //}
                    else
                    {
                        var sql = $@"select c.SYS_CHANGE_OPERATION, c.SYS_CHANGE_VERSION, c.SYS_CHANGE_CREATION_VERSION,
{string.Join(", ", table.KeyColumns.Select(c => "c." + c).Concat(table.OtherColumns.Select(c => "t." + c)))}
from CHANGETABLE (CHANGES {tableName}, @0) c
left outer join {tableName} t on ";
                        sql += string.Join(" and ", table.KeyColumns.Select(k => $"c.{k} = t.{k}"));
                        if (maxChangetrackingVersion > 0)
                        {
                            sql += Environment.NewLine + $" WHERE c.SYS_CHANGE_VERSION < {maxChangetrackingVersion}";
                        }
                        sql += Environment.NewLine + " ORDER BY coalesce(c.SYS_CHANGE_CREATION_VERSION, c.SYS_CHANGE_VERSION)";

                        Log.Debug($"Retrieving changes for table {tableName}");
                        Log.Trace($"{tableName}: {sql}");

                        db.OpenSharedConnection();
                        var cmd = db.CreateCommand(db.Connection, System.Data.CommandType.Text, sql, destinationVersion);

                        using var reader = cmd.ExecuteReader();
                        var numChanges = 0;

                        while (reader.Read())
                        {
                            var col = 0;
                            var change = new Change { Operation = ((string)reader[col])[0], Table = table };
                            col++;
                            var version = reader.GetInt64(col);
                            change.Version = version;
                            col++;
                            var creationVersion = reader.IsDBNull(col) ? version : reader.GetInt64(col);
                            change.CreationVersion = creationVersion;
                            col++;

                            if (!snapshotIsolationEnabled && Math.Min(version, creationVersion) > changeInfo.Version)
                            {
                                Log.Warn($"Ignoring change version {Math.Min(version, creationVersion)}");
                                continue;
                            }

                            for (int i = 0; i < table.KeyColumns.Count; i++, col++)
                            {
                                change.Keys[table.KeyColumns[i]] = reader.GetValue(col);
                            }

                            for (int i = 0; i < table.OtherColumns.Count; i++, col++)
                            {
                                change.Others[table.OtherColumns[i]] = reader.GetValue(col);

                                if (debugTables && source.DebugTables.Contains(table.Name))
                                {
                                    Log.Info($"Debug Table: {tableName} - Column {table.OtherColumns[i]} [DataTypeName: {reader.GetDataTypeName(col)}, FieldType: {reader.GetFieldType(col)}, Value: {change.Others[table.OtherColumns[i]]}]");
                                }
                            }

                            changes.Add(change);
                            numChanges++;
                        }

                        Log.Info($"Table {tableName} has {"change".ToQuantity(numChanges)}");
                    }

                }

                if (snapshotIsolationEnabled)
                    db.CompleteTransaction();
            }

            // 1. Sort by creation version to handle forign key constraints
            // 2. Sort by dependency order to handle foreign key constraints
            // 3. Sort by operation, Update before Insert (might not be alway valid)
            //      This handles the case where we have an index on IsActive or IsDeleted column

            changeInfo.Changes.AddRange(
                changes
                .OrderBy(c => c.CreationVersion)
                .ThenBy(c => c.Table.DependencyOrder)
                .ThenByDescending(c => c.Operation)
            );

            ComputeForeignKeyConstraintsToDisable(changeInfo);

            return changeInfo;
        }

        private List<Change> GetAllRows(Database db, TableInfo table, long version, int? skip = null, int? take = null)
        {
            var changes = new List<Change>();
            string tableName = table.Name;

            string sql;
            //            var sql = $@"select {string.Join(", ", table.KeyColumns.Select(c => c).Concat(table.OtherColumns.Select(c => c)))}
            //from {tableName} 
            //order by {string.Join(", ", table.KeyColumns.Select(c => c))}";

            if (skip.HasValue || take.HasValue)
            {
                //SELECT* FROM
                //(SELECT ROW_NUMBER() OVER (ORDER BY Id) as RowNumber, *
                //  FROM Customer) AS Tbl
                //  where RowNumber > 1
                var top = take.HasValue ? $" TOP ({take.Value}) " : "";
                var where = skip.HasValue ? $" WHERE RowNumber > {skip.Value}) " : "";

                var keyColumns = string.Join(", ", table.KeyColumns.Select(c => c));
                var allColumns = string.Join(", ", table.KeyColumns.Select(c => c).Concat(table.OtherColumns.Select(c => c)));
                sql = $@"SELECT {top} {allColumns}
FROM (SELECT ROW_NUMBER() OVER (ORDER BY {keyColumns}) as RowNumber, * FROM {tableName} ORDER BY {keyColumns})
{where}
ORDER BY RowNumber";
            }
            else
            {
                sql = $@"SELECT {string.Join(", ", table.KeyColumns.Select(c => c).Concat(table.OtherColumns.Select(c => c)))}
FROM {tableName} 
ORDER BY {string.Join(", ", table.KeyColumns.Select(c => c))}";
            }
            Log.Debug($"Retrieving changes for table {tableName}");
            Log.Trace($"{tableName}: {sql}");

            db.OpenSharedConnection();
            var cmd = db.CreateCommand(db.Connection, System.Data.CommandType.Text, sql);

            using (var reader = cmd.ExecuteReader())
            {
                var numChanges = 0;

                while (reader.Read())
                {
                    var col = 0;
                    var change = new Change { Operation = 'I', Table = table };
                    change.Version = version;
                    change.CreationVersion = int.MaxValue;

                    for (int i = 0; i < table.KeyColumns.Count; i++, col++)
                    {
                        change.Keys[table.KeyColumns[i]] = reader.GetValue(col);
                    }

                    for (int i = 0; i < table.OtherColumns.Count; i++, col++)
                    {
                        change.Others[table.OtherColumns[i]] = reader.GetValue(col);
                    }

                    changes.Add(change);
                    numChanges++;
                }

                Log.Info($"Table {tableName} has {"change".ToQuantity(numChanges)}");
            }

            return changes;
        }

        private void ComputeForeignKeyConstraintsToDisable(ChangeInfo changeInfo)
        {
            var changes = changeInfo.Changes.OrderBy(c => c.CreationVersion).ThenBy(c => c.Table.DependencyOrder).ToList();
            //var changes = changeInfo.Changes.OrderBy(c => c.CreationVersion).ThenBy(c => c.Table.Name).ToList();

            for (int i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                if (change.CreationVersion < change.Version || change.Operation == 'Z') // was inserted then later updated
                {
                    for (int j = i + 1; j < changes.Count; j++)
                    {
                        var intermediateChange = changes[j];
                        if (intermediateChange.CreationVersion > change.Version) // created later than last update to change
                            break;

                        if (intermediateChange.Operation != 'I' && intermediateChange.Operation != 'Z')
                        {
                            continue;
                        }

                        // let's look at intermediateChange if it collides with change
                        foreach (var fk in change.Table.ForeignKeyConstraints.Where(f => f.ReferencedTableName == intermediateChange.Table.Name))
                        {
                            var val = change.GetValue(fk.ColumnName);
                            var refVal = intermediateChange.GetValue(fk.ReferencedColumnName);
                            if (val != null && val.Equals(refVal))
                            {
                                // this foreign key constraint needs to be disabled
                                Log.Info($"Foreign key constraint {fk.ForeignKeyName} needs to be disabled for change #{i + 1} from version {change.CreationVersion} until version {intermediateChange.CreationVersion}");
                                change.ForeignKeyConstraintsToDisable[fk] = intermediateChange.CreationVersion;
                            }
                        }
                    }
                }
            }

            Log.Trace(messageFunc: () =>
            {
                return "Computed foreign key for the changes " + JSON.ToJSON(changeInfo);
            });
        }

        private void PerformChange(Database db, Change change)
        {
            var table = change.Table;
            var tableName = table.Name;
            var operation = change.Operation;

            switch (operation)
            {
                //// Repopulate
                //case 'Z':
                //    var deleteAllSql = string.Format("delete from {0}", tableName);
                //    Log.Debug($"Executing delete all: {deleteAllSql} ");

                //    if (!DryRun)
                //    {
                //        db.Execute(deleteAllSql);
                //    }
                //    break;

                // Insert
                case 'I':
                    var insertColumnNames = change.GetColumnNames();
                    //var insertSql = $"set IDENTITY_INSERT {tableName} ON; " +
                    //    string.Format("insert into {0} ({1}) values ({2}); ", tableName,
                    //    string.Join(", ", insertColumnNames),
                    //    string.Join(", ", Parameters(insertColumnNames.Count))) +
                    //    $"set IDENTITY_INSERT {tableName} OFF";

                    //var insertSql = string.Format("insert into {0} ({1}) values ({2})", tableName,
                    var insertSql = string.Format("insert into {0} ({1}) values ({2})",
                        tableName,
                        string.Join(", ", insertColumnNames),
                        string.Join(", ", Parameters(insertColumnNames.Count)));

                    var insertValues = change.GetValues();
                    if (table.HasIdentity)
                        insertSql = $"set IDENTITY_INSERT {tableName} ON; {insertSql}; set IDENTITY_INSERT {tableName} OFF";
                    Log.Debug($"Executing insert: {insertSql} ({FormatArgs(insertValues)})");

                    if (!DryRun)
                    {
                        try
                        {
                            db.Execute(insertSql, insertValues);
                        }
                        catch (SqlException sqlException)
                        {
                            if (sqlException.Number != 2627 || IgnoreDuplicateKeyInserts != true)
                            {
                                throw;
                            }
                        }
                    }

                    break;

                // Update
                case 'U':
                    var updateColumnNames = change.Others.Keys.ToList();
                    if (updateColumnNames.Count == 0)
                    {
                        Log.Info($"No columns to update for change {change.Table}");
                        break;
                    }

                    var updateSql = string.Format("update {0} set {1} where {2}",
                        tableName,
                        string.Join(", ", updateColumnNames.Select((c, i) => $"{c} = @{i + change.Keys.Count}")),
                        PrimaryKeys(change));
                    var updateValues = change.GetValues();
                    Log.Trace($"Executing update: {updateSql} ({FormatArgs(updateValues)})");

                    if (!DryRun)
                    {
                        try
                        {
                            db.Execute(updateSql, updateValues);
                        }
                        catch (System.Data.SqlClient.SqlException sqlException)
                        {
                            var doThrow = true;
                            if (sqlException.Message == "Operand type clash: nvarchar is incompatible with image")
                            {
                                var contentsColIndex = updateColumnNames.IndexOf("[Contents]");
                                if (contentsColIndex != -1 && Convert.IsDBNull(updateValues[contentsColIndex + change.Keys.Count]))
                                {
                                    updateValues[contentsColIndex + change.Keys.Count] = new byte[] { };
                                    doThrow = false;
                                    db.Execute(updateSql, updateValues);
                                }
                            }

                            if (doThrow) throw;
                        }
                    }

                    break;

                // Delete
                case 'D':
                    var deleteSql = string.Format("delete from {0} where {1}", tableName, PrimaryKeys(change));
                    var deleteValues = change.Keys.Values.ToArray();
                    Log.Trace($"Executing delete: {deleteSql} ({FormatArgs(deleteValues)})");

                    if (!DryRun)
                    {
                        db.Execute(deleteSql, deleteValues);
                    }

                    break;
            }
        }

        /// <summary>
        /// Pass changeTrackingVersion as 0 to select version from db
        /// </summary>
        /// <param name="db"></param>
        /// <param name="flushChange"></param>
        /// <param name="changeTrackingVersion"></param>
        /// <param name="dataInSubChanges"></param>
        /// <param name="source"></param>
        /// <returns></returns>
        private void FlushTable(Database db,
            Change flushChange,
            ref long changeTrackingVersion,
            bool dataInSubChanges = false,
            DatabaseInfo source = null)
        {
            var table = flushChange.Table;
            var tableName = table.Name;

            var deleteAllSql = string.Format("delete from {0}", tableName);
            //var deleteAllSql = string.Format("truncate  {0}", tableName);
            Log.Trace($"Executing delete all: {deleteAllSql} ");

            if (!DryRun)
            {
                db.Execute(deleteAllSql);
            }

            if (table.HasIdentity)
            {
                var insertSql = $"set IDENTITY_INSERT {tableName} ON;";
                Log.Trace($"IDENTITY INSERT ON: {insertSql}");

                if (!DryRun)
                {
                    db.Execute(insertSql);
                }
            }

            if (dataInSubChanges)
            {
                foreach (var change in flushChange.SubChanges)
                {
                    var insertColumnNames = change.GetColumnNames();
                    //var insertSql = $"set IDENTITY_INSERT {tableName} ON; " +
                    //    string.Format("insert into {0} ({1}) values ({2}); ", tableName,
                    //    string.Join(", ", insertColumnNames),
                    //    string.Join(", ", Parameters(insertColumnNames.Count))) +
                    //    $"set IDENTITY_INSERT {tableName} OFF";

                    var insertSql = string.Format("insert into {0} ({1}) values ({2})",
                        tableName,
                        string.Join(", ", insertColumnNames),
                        string.Join(", ", Parameters(insertColumnNames.Count)));

                    var insertValues = change.GetValues();
                    Log.Trace($"Executing insert: {insertSql} ({FormatArgs(insertValues)})");

                    if (!DryRun)
                    {
                        db.Execute(insertSql, insertValues);
                    }
                }
            }
            else
            {
                var allColumns = table.KeyColumns.Concat(table.OtherColumns);
                var keyColumns = string.Join(", ", table.KeyColumns.Select(c => c));
                var selectAllColumns = string.Join(", ", allColumns.Select(c => c));
                var allColumnsCount = allColumns.Count();

                var insertSql = string.Format("insert into {0} ({1}) values ({2})",
                        tableName,
                        string.Join(", ", selectAllColumns),
                        string.Join(", ", Parameters(allColumnsCount)));

                //Log.Trace($"Executing insert: {insertSql} ({FormatArgs(insertValues)})");

                if (!DryRun && false)
                {
                    int start = 0;
                    int increment = 1000;
                    var end = start;

                    Log.Debug($"Retrieving all records for table {tableName}");

                    string orderByColumns;
                    if (allColumns.Any(x => x.ToLower() == "CreatedOn"))
                    {
                        orderByColumns = "T.[CreatedOn], " + string.Join(", ", table.KeyColumns.Select(c => $"T.{c}"));
                    }
                    else
                    {
                        orderByColumns = string.Join(", ", table.KeyColumns.Select(c => $"T.{c}"));
                    }

                    string sql = $@"SELECT {selectAllColumns}
                        FROM (
SELECT ROW_NUMBER() OVER (ORDER BY {orderByColumns}) as RowNumber, T.*
FROM {tableName} as T
LEFT OUTER JOIN CHANGETABLE (CHANGES {tableName}, 0) AS CT  ON {string.Join(" and ", table.KeyColumns.Select(k => $"CT.{k} = T.{k}"))}
WHERE CT.SYS_CHANGE_VERSION <= @2 or CT.SYS_CHANGE_VERSION IS NULL
) AS tbl
WHERE RowNumber > @0 AND RowNumber <= @1
ORDER BY RowNumber";

                    //                    if (changeTrackingVersion != 0)
                    //                    {
                    //                    }
                    //                    else
                    //                    {
                    //                        sql = $@"SELECT {selectAllColumns}
                    //FROM (SELECT ROW_NUMBER() OVER (ORDER BY {orderByColumns}) as RowNumber, * FROM {tableName}) AS tbl
                    //WHERE RowNumber > @0 AND RowNumber <= @1
                    //ORDER BY RowNumber";
                    //                    }

                    using (var sourceDb = GetDatabase(source.ConnectionString, DatabaseType.SqlServer2008))
                    {
                        sourceDb.OpenSharedConnection();

                        if (changeTrackingVersion == 0)
                        {
                            changeTrackingVersion = sourceDb.ExecuteScalar<long>("select CHANGE_TRACKING_CURRENT_VERSION()");
                        }

                        while (start == end)
                        {
                            end = start + increment;
                            //                            var top = take.HasValue ? $" TOP ({take.Value}) " : "";
                            //                            var where = skip > 0 ? $" WHERE RowNumber > {skip}) " : "";

                            //                            var sql = $@"SELECT {top} {selectAllColumns}
                            //FROM (SELECT ROW_NUMBER() OVER (ORDER BY {keyColumns}) as RowNumber, * FROM {tableName} ORDER BY {keyColumns})
                            //{where}
                            //ORDER BY RowNumber";

                            Log.Trace($"{tableName}: {sql}");

                            var cmd = sourceDb.CreateCommand(
                                sourceDb.Connection,
                                System.Data.CommandType.Text,
                                sql,
                                start,
                                end,
                                changeTrackingVersion);

                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    var insertValues = new object[allColumnsCount];
                                    reader.GetValues(insertValues);

                                    db.Execute(insertSql, insertValues);
                                    start++;
                                }
                            }
                        }
                    }

                    Log.Debug($"Inserted into {tableName} total of {start} record");
                }
                else if (!DryRun)
                {
                    Log.Debug($"Retrieving all records for table {tableName}");

                    string orderByColumns;
                    if (allColumns.Any(x => x.ToLower() == "CreatedOn"))
                    {
                        orderByColumns = "[CreatedOn], " + keyColumns;
                    }
                    else
                    {
                        orderByColumns = keyColumns;
                    }

                    string sql = $@"SELECT {selectAllColumns} FROM {tableName} ORDER BY {orderByColumns}";
                    var start = 0;

                    using (var sourceDb = GetDatabase(source.ConnectionString, DatabaseType.SqlServer2008))
                    {
                        sourceDb.OpenSharedConnection();

                        Log.Trace($"{tableName}: {sql}");

                        var cmd = sourceDb.CreateCommand(
                            sourceDb.Connection,
                            System.Data.CommandType.Text,
                            sql);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var insertValues = new object[allColumnsCount];
                                reader.GetValues(insertValues);

                                db.Execute(insertSql, insertValues);
                                start++;
                            }
                        }

                    }

                    Log.Debug($"Inserted into {tableName} total of {start} record");
                }

            }

            if (table.HasIdentity)
            {
                var insertSql = $"set IDENTITY_INSERT {tableName} OFF;";
                Log.Trace($"IDENTITY INSERT OFF: {insertSql}");

                if (!DryRun)
                {
                    db.Execute(insertSql);
                }
            }

        }


        private static string FormatArgs(object[] args) => string.Join(", ", args.Select((a, i) => $"@{i} = {a}"));

        private static string PrimaryKeys(Change change) =>
            string.Join(" and ", change.Keys.Keys.Select((c, i) => $"{c} = @{i}"));

        private static IEnumerable<string> Parameters(int n) => Enumerable.Range(0, n).Select(c => "@" + c);

        /// <summary>
        /// Gets current version from SyncInfo, falls back to CHANGE_TRACKING_CURRENT_VERSION if SyncInfo table is not found
        /// </summary>
        /// <param name="dbInfo"></param>
        /// <returns>Returns current version or -1 if Change tracking not enabled in database</returns>
        private long GetCurrentVersion(DatabaseInfo dbInfo)
        {
            try
            {
                using var db = GetDatabase(dbInfo.ConnectionString, DatabaseType.SqlServer2005);
                var syncInfoTableExists = db.ExecuteScalar<string>("select top(1) name from sys.tables where name ='SyncInfo'") != null;
                long currentVersion;

                if (!syncInfoTableExists)
                {
                    Log.Info($"SyncInfo table does not exist in database {dbInfo.Name}");
                    currentVersion = db.ExecuteScalar<long?>("select CHANGE_TRACKING_CURRENT_VERSION()") ?? -1;
                    if (currentVersion < 0)
                    {
                        Log.Info($"Change tracking not enabled in database {dbInfo.Name}, assuming version 0");
                        //currentVersion = 0;
                    }
                    else
                        Log.Info($"Database {dbInfo.Name} is at version {currentVersion}");
                }
                else
                {
                    currentVersion = db.ExecuteScalar<long>("select top(1) Version from SyncInfo");
                    Log.Info($"Database {dbInfo.Name} is at version {currentVersion}");
                }

                return currentVersion;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
            {
                Log.Error(ex, $"Error getting current version of destination database {dbInfo.Name}. Skipping this destination.");
                Error = true;
                return -1;
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }
    }

}
