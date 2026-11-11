using System.Runtime.InteropServices;
using System.Text;

namespace MightyCode.Core;

/// <summary>
/// The few SQLite calls the app's read-only lists need (Antigravity's conversations, opencode's and Hermes's sessions), bound at run time
/// to the system's own library (Windows ships <c>winsqlite3.dll</c>; macOS and Linux have <c>libsqlite3</c>), so the app carries no
/// database package. Read-only: a database is opened with <c>mode=ro</c> (read through the writer's WAL while the CLI runs) and, when
/// that fails, as an <c>immutable</c> file (<see cref="ReadOnly"/>).
/// </summary>
internal sealed class SystemSqlite
{
    private const int Ok = 0, Row = 100, Done = 101, OpenReadOnly = 0x1, OpenUri = 0x40, OpenNoMutex = 0x8000;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int OpenV2(byte[] filename, out nint db, int flags, nint vfs);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Close(nint db);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int BusyTimeout(nint db, int milliseconds);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int PrepareV2(nint db, byte[] sql, int bytes, out nint statement, nint tail);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int BindText(nint statement, int index, byte[] text, int bytes, nint destructor);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int StepStatement(nint statement);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint ColumnText(nint statement, int column);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int ColumnBytes(nint statement, int column);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int FinalizeStatement(nint statement);
    private readonly OpenV2 open; private readonly Close close; private readonly BusyTimeout busy; private readonly PrepareV2 prepare; private readonly BindText bind;
    private readonly StepStatement step; private readonly ColumnText text; private readonly ColumnBytes length; private readonly FinalizeStatement finalize;
    private static readonly Lazy<SystemSqlite?> Shared = new(Bind);
    /// <summary>SQLITE_TRANSIENT: SQLite copies a bound value before the call returns.</summary>
    private static readonly nint Transient = -1;

    private SystemSqlite(nint library)
    {
        T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        open = Get<OpenV2>("sqlite3_open_v2"); close = Get<Close>("sqlite3_close"); busy = Get<BusyTimeout>("sqlite3_busy_timeout");
        prepare = Get<PrepareV2>("sqlite3_prepare_v2"); bind = Get<BindText>("sqlite3_bind_text"); step = Get<StepStatement>("sqlite3_step");
        text = Get<ColumnText>("sqlite3_column_text"); length = Get<ColumnBytes>("sqlite3_column_bytes"); finalize = Get<FinalizeStatement>("sqlite3_finalize");
    }

    /// <summary>The system's SQLite, or null when it has none the app may load.</summary>
    internal static SystemSqlite? Load() => Shared.Value;

    private static SystemSqlite? Bind()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "winsqlite3.dll" } : OperatingSystem.IsMacOS() ? ["/usr/lib/libsqlite3.dylib", "libsqlite3.dylib"] : ["libsqlite3.so.0", "libsqlite3.so"];
        foreach (var name in names)
        {
            // Windows' own copy only, from System32: never a winsqlite3.dll planted beside the app or in the working folder.
            var loaded = OperatingSystem.IsWindows()
                ? NativeLibrary.TryLoad(name, typeof(SystemSqlite).Assembly, DllImportSearchPath.System32, out var library)
                : NativeLibrary.TryLoad(name, out library);
            if (!loaded) continue;
            try { return new SystemSqlite(library); }
            catch (EntryPointNotFoundException) { NativeLibrary.Free(library); }
        }
        return null;
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");

    /// <summary>
    /// <paramref name="sql"/> on the database file <paramref name="path"/>, read-only: through <c>mode=ro</c> first, then as an
    /// <c>immutable</c> file. Null when the file is not a regular file, there is no SQLite, or neither way read the query to its end.
    /// </summary>
    internal static List<string?[]>? ReadOnly(string path, string sql, int columns, params string[] parameters)
    {
        if (!RecordFiles.Regular(path) || Load() is not { } sqlite) return null;
        foreach (var mode in new[] { "mode=ro", "immutable=1" })
            if (sqlite.Query(new Uri(Path.GetFullPath(path)).AbsoluteUri + "?" + mode, sql, columns, parameters) is { } rows) return rows;
        return null;
    }

    /// <summary>
    /// Every row's first <paramref name="columns"/> columns as text, or null when the database could not be opened or read to the end.
    /// <paramref name="parameters"/> are bound in order to the statement's <c>?</c> placeholders, as text.
    /// </summary>
    internal List<string?[]>? Query(string uri, string sql, int columns, params string[] parameters)
    {
        if (open(Utf8(uri), out var db, OpenReadOnly | OpenUri | OpenNoMutex, 0) != Ok || db == 0) { if (db != 0) close(db); return null; }
        try
        {
            busy(db, 250);
            if (prepare(db, Utf8(sql), -1, out var statement, 0) != Ok || statement == 0) { if (statement != 0) finalize(statement); return null; }
            try
            {
                for (var index = 0; index < parameters.Length; index++)
                {
                    var value = Encoding.UTF8.GetBytes(parameters[index]);
                    if (bind(statement, index + 1, value, value.Length, Transient) != Ok) return null;
                }
                var rows = new List<string?[]>(); int result;
                while ((result = step(statement)) == Row)
                {
                    var row = new string?[columns];
                    for (var column = 0; column < columns; column++)
                    {
                        var pointer = text(statement, column);
                        row[column] = pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer, length(statement, column));
                    }
                    rows.Add(row);
                }
                return result == Done ? rows : null;
            }
            finally { finalize(statement); }
        }
        finally { close(db); }
    }
}
