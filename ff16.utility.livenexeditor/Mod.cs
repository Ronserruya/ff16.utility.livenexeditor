using ff16.utility.livenexeditor.Configuration;
using ff16.utility.livenexeditor.Template;

using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

using Hooks = Reloaded.Hooks;
using System.Reactive.Linq;

using FF16Tools.Files.Nex;
using FF16Tools.Files.Nex.Entities;
using FF16Framework.Interfaces.Nex;

using Windows.Win32;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Reloaded.Hooks.Definitions;
using System.Runtime.InteropServices;
using System.Data.Common;
namespace ff16.utility.livenexeditor;

/// <summary>
/// Your mod logic goes here.
/// </summary>
public class Mod : ModBase // <= Do not Remove.
{
    /// <summary>
    /// Provides access to the mod loader API.
    /// </summary>
    private readonly IModLoader _modLoader;

    /// <summary>
    /// Provides access to the Reloaded.Hooks API.
    /// </summary>
    /// <remarks>This is null if you remove dependency on Reloaded.SharedLib.Hooks in your mod.</remarks>
    private readonly IReloadedHooks? _hooks;

    /// <summary>
    /// Provides access to the Reloaded logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Entry point into the mod, instance that created this class.
    /// </summary>
    private readonly IMod _owner;

    /// <summary>
    /// Provides access to this mod's configuration.
    /// </summary>
    private Config _configuration;

    /// <summary>
    /// The configuration of the currently executing mod.
    /// </summary>
    private readonly IModConfig _modConfig;

    private FileSystemWatcher? _watcher;

    private HashSet<string> _trackedSqlFiles = new();

    private Dictionary<string, int> _lastSqlChangeRead = new();

    private static readonly string _sqlite_changes_table = "_liveNex_changes";
    
    private bool _incompatibleTableAlertShown = false;

    private IDisposable? _fileChangedObservable;

    private static JsonSerializerOptions _jsonSerializerOptions = new() { Converters = { JsonByteArrayConverter.Instance } };

    public WeakReference<INextExcelDBApiManaged> _managedNexApi;

    private IHook<ExitProcess> _exitProcessHook;

    private void ExitProcessImpl(uint uexitcode)
    {
        _fileChangedObservable?.Dispose();
        _watcher?.Dispose();
        TearDownSql();
        _exitProcessHook.OriginalFunction(uexitcode);
    }

    [Hooks.Definitions.X64.Function(Hooks.Definitions.X64.CallingConventions.Microsoft)]
    [Hooks.Definitions.X86.Function(Hooks.Definitions.X86.CallingConventions.Cdecl)]
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ExitProcess(uint uExitCode);

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

#if DEBUG
        Debugger.Launch();
#endif

        _logger.WriteLine($"[{_modConfig.ModId}] Initializing..", _logger.ColorGreen);
        _configuration.ThrottleSeconds = Math.Max(0.5f, _configuration.ThrottleSeconds);

        _managedNexApi = _modLoader.GetController<INextExcelDBApiManaged>();
        if (!_managedNexApi.TryGetTarget(out INextExcelDBApiManaged managedNextExcelDBApi))
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Could not get INextExcelDBApi. Is the FFXVI Mod Framework installed/loaded?");
            return;
        }

        managedNextExcelDBApi.OnNexLoaded += NextExcelDBApi_OnNexLoaded;

        var kernel32 = PInvoke.GetModuleHandle("kernel32.dll");
        var address = PInvoke.GetProcAddress(kernel32, "ExitProcess");
        if (address != IntPtr.Zero)
            _exitProcessHook = _hooks.CreateHook<ExitProcess>(ExitProcessImpl, address).Activate();
    }

    private void StartMonitoringFolder()
    {

        List<string> fileTypes = new List<string>();

        if (_configuration.MonitorNxdChanges)
        {
            if (_configuration.MonitorPath == "")
            {
                _logger.WriteLine($"[{_modConfig.ModId}] `MonitorPath` is false, not watching for nxd changes!");
                return;
            }
            fileTypes.Add("*.nxd");
        }
        if (_configuration.MonitorSqlChanges)
        {
            if (_configuration.MonitorPath == "")
            {
                _logger.WriteLine($"[{_modConfig.ModId}] `MonitorPath` is false, not watching for sqlite changes!");
                return;
            }
            fileTypes.Add("*.sqlite");
            var sqlFiles = Directory.GetFiles(_configuration.MonitorPath, "*.sqlite");
            _trackedSqlFiles = new HashSet<string>(sqlFiles);
            foreach (var sqlFile in sqlFiles)
            {
                PrepareSqlDB(sqlFile);
            }
        }
        if (!_configuration.MonitorSqlChanges && !_configuration.MonitorNxdChanges)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] `MonitorSqlChanges` and `MonitorSqlChanges` are false, not watching for any changes!");
            return;
        }

        _watcher = new FileSystemWatcher(_configuration.MonitorPath)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        foreach (string fileType in fileTypes)
        {
            _watcher.Filters.Add(fileType);
        }

        _fileChangedObservable = Observable.FromEventPattern<FileSystemEventHandler, FileSystemEventArgs>(
            handler => _watcher.Changed += handler,
            handler => _watcher.Changed -= handler
        )
        .Merge(Observable.FromEventPattern<FileSystemEventHandler, FileSystemEventArgs>(
            handler => _watcher.Created += handler,
            handler => _watcher.Created -= handler
        ))

        .Select(e => (
            e.EventArgs.FullPath,
            e.EventArgs.Name,
            e.EventArgs.ChangeType,
            FileType: e.EventArgs.Name.Split('.').Last()
        ))
        .GroupBy(eventData => eventData.FileType)
        .Subscribe(grouped =>
        {
            grouped
                .Do(eventData =>
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] Detected {eventData.FileType} change: {eventData.FullPath}, waiting {_configuration.ThrottleSeconds} seconds for successive changes before processing...");
                })
                .Throttle(TimeSpan.FromSeconds(_configuration.ThrottleSeconds))
                .Subscribe(eventData =>
                {
                    switch (eventData.FileType)
                    {
                        case "nxd":
                            ProcessNxdFileChange(eventData.FullPath, eventData.Name);
                            break;
                        case "sqlite":
                            if (eventData.ChangeType == WatcherChangeTypes.Created)
                            {
                                _logger.WriteLine($"[{_modConfig.ModId}] Ignoring newly created sqlite file {eventData.Name}");
                                break;
                            }
                            ProcessSqliteChanges(eventData.FullPath, eventData.Name);
                            break;
                    }
                });
        });

        _logger.WriteLine($"[{_modConfig.ModId}] Started watching for changes at path: {_configuration.MonitorPath}", _logger.ColorGreen);
    }


    /// <summary>
    /// Fired when the game has loaded all nex tables.
    /// </summary>
    private unsafe void NextExcelDBApi_OnNexLoaded()
    {
        StartMonitoringFolder();
    }

    private void PrintIncompatibleTableError(string tableName, List<string> columns) {
        if (_incompatibleTableAlertShown)
            return;
        _incompatibleTableAlertShown = true;
        _logger.WriteLine(
            $"[{_modConfig.ModId}] Detected incompatiblity when trying to edit table {tableName}:\n" +
            $"the table contains the following columns that aren't recognized: [{String.Join(", ", columns)}].\n" +
            $"this can happen when the sql table was generated by a different version of FF16Tools than the one used by this mod.\n" +
            $"the detected change, or any other incompatible change would not be applied, you can attempt the following:\n" +
            $"* Copy all .layout files from the NEX folder under your version of FF16Tools to the one under this mod, OR\n" +
            $"* Turn on `Attempt editing by index` in the configuration menu, which will try editing by index instead of column name, altough that might not work well",
            _logger.ColorRed
        );
    }

    private unsafe void ProcessNxdFileChange(string filePath, string name)
    {
        _logger.WriteLine($"[{_modConfig.ModId}] Processing changes for nxd file: {name}", _logger.ColorBlue);

        _managedNexApi.TryGetTarget(out var nextExcelDBApi);

        string nexTableName = name.Split(".")[0];
        if (!Enum.TryParse<NexTableIds>(nexTableName, ignoreCase: false, out var tableEnum))
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Unable to fetch table for file: {name}", _logger.ColorRed);
            return;
        }
        INexTable? table = nextExcelDBApi.GetTable(tableEnum);


        NexTableLayout layout = TableMappingReader.ReadTableLayout(nexTableName, new Version(1, 0, 3));

        NexDataFile nexFile = NexDataFile.FromFile(filePath);
        List<NexRowInfo> fileRows = nexFile.RowManager.GetAllRowInfos();

        foreach (NexRowInfo fileRow in fileRows)
        {

            INexRow? gameRow = table.GetRow(fileRow.Key, fileRow.Key2, fileRow.Key3);
            if (gameRow is null)
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Could not get {nexTableName} row with keys: ({fileRow.Key}, {fileRow.Key2}, {fileRow.Key3}), skipping", _logger.ColorRed);
                continue;
            }

            var fileRowData = NexUtils.ReadRow(layout, nexFile.Buffer, fileRow.RowDataOffset);

            for (int j = 0; j < fileRowData.Count; j++)
            {

                var col = layout.Columns.ElementAt(j);
                if (col.Key.StartsWith("Comment")) // Skip Comments
                    continue;

                switch (col.Value.Type)
                {
                    case NexColumnType.Byte:
                        gameRow.SetByte((uint)col.Value.Offset, (byte)fileRowData[j]);
                        break;
                    case NexColumnType.SByte:
                        gameRow.SetSByte((uint)col.Value.Offset, (sbyte)fileRowData[j]);
                        break;
                    case NexColumnType.Short:
                        gameRow.SetInt16((uint)col.Value.Offset, (Int16)fileRowData[j]);
                        break;
                    case NexColumnType.UShort:
                        gameRow.SetUInt16((uint)col.Value.Offset, (UInt16)fileRowData[j]);
                        break;
                    case NexColumnType.Int:
                        gameRow.SetInt32((uint)col.Value.Offset, (Int32)fileRowData[j]);
                        break;
                    case NexColumnType.UInt:
                        gameRow.SetUInt32((uint)col.Value.Offset, (UInt32)fileRowData[j]);
                        break;
                    case NexColumnType.Float:
                        gameRow.SetSingle((uint)col.Value.Offset, (float)fileRowData[j]);
                        break;
                    case NexColumnType.IntArray:
                        HandleFileArrayChange(gameRow, col.Value, (int[])fileRowData[j], gameRow.GetIntArrayView((uint)col.Value.Offset));
                        break;
                    case NexColumnType.FloatArray:
                        HandleFileArrayChange(gameRow, col.Value, (float[])fileRowData[j], gameRow.GetSingleArrayView((uint)col.Value.Offset));
                        break;
                    case NexColumnType.ByteArray:
                        HandleFileArrayChange(gameRow, col.Value, (byte[])fileRowData[j], gameRow.GetByteArrayView((uint)col.Value.Offset));
                        break;
                    default:
                        break;
                }
            }
        }

        _logger.WriteLine($"[{_modConfig.ModId}] Applied {nexTableName} changes!", _logger.ColorGreen);
    }

    private void ProcessSqliteChanges(string filePath, string name)
    {
        _logger.WriteLine($"[{_modConfig.ModId}] Processing changes for sql file: {name}");

        using var connection = new SqliteConnection($"Data Source={filePath}");
        connection.Open();
        var result = GetChangedTables(connection, _lastSqlChangeRead[filePath]);
        if (result is null)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Found no actual changes, skipping.");
            return;
        }

        string[] changedTables = result.Value.Item2;

        foreach (var table in changedTables)
        {
            ProcessTableChanges(connection, table, _lastSqlChangeRead[filePath]);
        }

        _lastSqlChangeRead[filePath] = result.Value.Item1;
    }

    private void ProcessTableChanges(SqliteConnection connection, string tableName, int previousReadChangeId)
    {

        _logger.WriteLine($"[{_modConfig.ModId}] Processing changes to table: {tableName}...", _logger.ColorBlue);

        _managedNexApi.TryGetTarget(out var nextExcelDBApi);

        INexTable? table = nextExcelDBApi.GetTable(Enum.Parse<NexTableIds>(tableName));
        NexTableLayout layout = TableMappingReader.ReadTableLayout(tableName, new Version(1, 0, 0));
        FF16Tools.Files.Nex.NexTableType tableType = layout.Type;

        var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
        SELECT a.* FROM {tableName} a join 
        (SELECT DISTINCT Key1, Key2, Key3 FROM {_sqlite_changes_table} WHERE id>{previousReadChangeId} and tableName='{tableName}') b ON 
        (
            a.Key==b.Key1 and 
            {(layout.Type == FF16Tools.Files.Nex.NexTableType.DoubleKeyed ? "a.Key2" : 0)}==b.Key2 and 
            {(layout.Type == FF16Tools.Files.Nex.NexTableType.TripleKeyed ? "a.Key3" : 0)}==b.Key3 
        );
        ";

        int rowCount = 0;
        uint key1, key2, key3;
        using (var reader = cmd.ExecuteReader())
        {
            bool tryByIndex = false;
            var cols = reader.GetColumnSchema();
            var diff = cols.Select(col => col.ColumnName).Except(layout.Columns.Keys).Except(new[] { "Key", "Key2", "Key3" }).ToList();
            if (diff.Count > 0) {
                if (_configuration.AttemptEditByIndex) {
                    tryByIndex = true;
                    _logger.WriteLine($"[{_modConfig.ModId}] Attempting to edit rows by index...", _logger.ColorLightBlue);
                }
                else {
                    PrintIncompatibleTableError(tableName, diff);
                    return;
                }
            }

            while (reader.Read())
            {
                key1 = (uint)(long)reader["Key"];
                key2 = (layout.Type == FF16Tools.Files.Nex.NexTableType.DoubleKeyed ? (uint)(long)reader["Key2"] : 0);
                key3 = (layout.Type == FF16Tools.Files.Nex.NexTableType.TripleKeyed ? (uint)(long)reader["Key3"] : 0);

                var row = table.GetRow(key1, key2, key3);

                if (row is null)
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] Failed to get find game row with keys ({key1}, {key2}, {key3}), skipping change.", _logger.ColorRed);
                    continue;
                }
                UpdateSingleRowFromReader(reader, layout, row, tryByIndex);
                rowCount++;
            }
        }


        _logger.WriteLine($"[{_modConfig.ModId}] Updated {rowCount} nex records from table: {tableName}!", _logger.ColorGreen);

    }

    private unsafe void UpdateSingleRowFromReader(SqliteDataReader reader, NexTableLayout layout, INexRow gameRow, bool tryByIndex)
    {
        int index = layout.Type switch
        {
            FF16Tools.Files.Nex.NexTableType.SingleKeyed => 0,
            FF16Tools.Files.Nex.NexTableType.DoubleKeyed => 1,
            FF16Tools.Files.Nex.NexTableType.TripleKeyed => 2,
        };

        foreach (NexStructColumn column in layout.Columns.Values)
        {
            index++;

            if (column.Name == "Comment") { continue; }

            object val;
            if (tryByIndex)
                val = reader[index];
            else val = reader[column.Name];

            switch (column.Type)
            {
                case NexColumnType.Byte:
                    gameRow.SetByte((uint)column.Offset, val is not DBNull ? (byte)(long)val : (byte)0);
                    break;
                case NexColumnType.SByte:
                    gameRow.SetSByte((uint)column.Offset, val is not DBNull ? (sbyte)(long)val : (sbyte)0);
                    break;
                case NexColumnType.Short:
                    gameRow.SetInt16((uint)column.Offset, val is not DBNull ? (short)(long)val : (short)0);
                    break;
                case NexColumnType.UShort:
                    gameRow.SetUInt16((uint)column.Offset, val is not DBNull ? (ushort)(long)val : (ushort)0);
                    break;
                case NexColumnType.Int:
                    gameRow.SetInt32((uint)column.Offset, val is not DBNull ? (int)(long)val : 0);
                    break;
                case NexColumnType.UInt:
                    gameRow.SetUInt32((uint)column.Offset, val is not DBNull ? (uint)(long)val : 0u);
                    break;
                case NexColumnType.Float:
                    gameRow.SetSingle((uint)column.Offset, val is not DBNull ? (float)(double)val : 0f);
                    break;
                case NexColumnType.FloatArray:
                    if (val is not DBNull)
                        HandleSqlArrayChange(gameRow, column, val, gameRow.GetSingleArrayView((uint)column.Offset));
                    break;
                case NexColumnType.ByteArray:
                    if (val is not DBNull)
                        HandleSqlArrayChange(gameRow, column, val, gameRow.GetByteArrayView((uint)column.Offset));
                    break;
                case NexColumnType.IntArray:
                    if (val is not DBNull)
                        HandleSqlArrayChange(gameRow, column, val, gameRow.GetIntArrayView((uint)column.Offset));
                    break;
                default:
                    break;
            }
        }
    }

    private void HandleSqlArrayChange<T>(INexRow gameRow, NexStructColumn column, object val, Span<T> existingArray)
    {
        if (existingArray.IsEmpty) { return; }
        var newArray = JsonSerializer.Deserialize<T[]>((string)val, typeof(T) == typeof(byte) ? _jsonSerializerOptions : null);

        if (newArray.Length != existingArray.Length)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Array in column {column.Name} changed length ({existingArray.Length} -> {newArray.Length}), ignoring change!", _logger.ColorRed);
            return;
        }

        for (int i = 0; i < existingArray.Length; i++)
        {
            existingArray[i] = newArray[i];
        }
    }

    private void HandleFileArrayChange<T>(INexRow gameRow, NexStructColumn column, T[] val, Span<T> existingArray)
    {
        if (val == null || existingArray.IsEmpty) { return; }
        if (val.Length != existingArray.Length) { return; } // Dont log warning for file changes

        for (int i = 0; i < existingArray.Length; i++)
        {
            existingArray[i] = val[i];
        }
    }

    private void PrepareSqlDB(string sqlFile)
    {
        _logger.WriteLine($"[{_modConfig.ModId}] Preparing required SQL internal tables for monitoring on sql db: {sqlFile}", _logger.ColorBlue);

        _lastSqlChangeRead[sqlFile] = 0;
        using var _connection = new SqliteConnection($"Data Source={sqlFile}");
        _connection.Open();
        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();

        cmd.CommandText = $"DROP TABLE IF EXISTS {_sqlite_changes_table};";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
        CREATE TABLE {_sqlite_changes_table} (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            tableName TEXT,
            Key1 INTEGER,
            Key2 INTEGER,
            Key3 INTEGER
        )";
        cmd.ExecuteNonQuery();

        _logger.WriteLine($"[{_modConfig.ModId}] Created internal history table...", _logger.ColorBlue);

        var tables = GetAllTables(_connection);

        foreach (var table in tables)
        {

            NexTableLayout layout = TableMappingReader.ReadTableLayout(table, new Version(1, 0, 0));
            if (layout is null)
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Found unrecognized table {table} without matching nex table, skipping", _logger.ColorRedLight);
                continue;
            }

            cmd.CommandText = $@"DROP TRIGGER IF EXISTS _{table}_update_trigger";
            cmd.ExecuteNonQuery();

            cmd.CommandText = $@"
            CREATE TRIGGER _{table}_update_trigger
            AFTER UPDATE ON {table}
            FOR EACH ROW
            BEGIN
                INSERT INTO {_sqlite_changes_table} (tableName, Key1, Key2, Key3)
                VALUES (
                    '{table}',
                    OLD.Key,
                    {(layout.Type == FF16Tools.Files.Nex.NexTableType.SingleKeyed ? 0 : "OLD.Key2")},
                    {(layout.Type == FF16Tools.Files.Nex.NexTableType.TripleKeyed ? "OLD.Key3" : 0)}
                );
            END";
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();

        _logger.WriteLine($"[{_modConfig.ModId}] Created internal triggers...", _logger.ColorBlue);

        _logger.WriteLine($"[{_modConfig.ModId}] Finished setup for sql db: {sqlFile}", _logger.ColorGreen);
    }

    private static List<string> GetAllTables(SqliteConnection connection)
    {
        var tableNames = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '\_%' ESCAPE '\';";

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                tableNames.Add(reader.GetString(0));
        }

        return tableNames;
    }

    private static (int, string[])? GetChangedTables(SqliteConnection connection, int lastChangeId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT max(id) as lastChangeId, group_concat(tableName, ',') as changedTables FROM 
            (SELECT max(id) as id, tableName from {_sqlite_changes_table} where id>{lastChangeId} group by tableName)
        ";

        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                int newLastChangeId = reader.GetInt32(0);
                var changedTables = reader.GetString(1).Split(',');
                return (newLastChangeId, changedTables);
            }
        }

        return null;
    }

    private void TearDownSql()
    {
        Task.WaitAll(_trackedSqlFiles.Select(sqlFile => Task.Run(() =>
        {
            using var connection = new SqliteConnection($"Data Source={sqlFile}");
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS {_sqlite_changes_table}";
            cmd.ExecuteNonQuery();

            var tables = GetAllTables(connection);
            foreach (var table in tables)
            {
                cmd = connection.CreateCommand();
                cmd.CommandText = $@"DROP TRIGGER IF EXISTS _{table}_update_trigger";
                cmd.ExecuteNonQuery();
            }
        })));
        _logger.WriteLine($"[{_modConfig.ModId}] Cleaned internal SQL changes", _logger.ColorBlue);
    }

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        // Apply settings from configuration.
        // ... your code here.
        _configuration = configuration;
        _logger.WriteLine($"[{_modConfig.ModId}] Live config update is not supported!", _logger.ColorRed);
    }
    #endregion

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}