using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.Data.SQLite;

namespace BIManage.AI.Terminal.Tools.QueryAuditLog;

/// <summary>
/// Handler for the <c>query_audit_log</c> tool. Reads <c>audit_log</c> rows from
/// <c>%LOCALAPPDATA%\BIManageRevit\Logs\bimanage.db</c> with the requested filters.
/// </summary>
/// <remarks>
/// This tool does not require Revit's UI thread (SQLite is thread-safe enough for our read-only
/// usage), so <see cref="WaitForCompletion"/> always returns immediately after <see cref="Execute"/>.
/// The interface contract is preserved purely to fit the existing tool-registry pipeline.
/// </remarks>
internal sealed class QueryAuditLogEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);

    private int _dayRange = 7;
    private string? _mode;
    private string? _commandName;
    private string? _userName;
    private int _limit = 50;

    public AuditLogQueryResult? Result { get; private set; }

    public void SetParameters(int dayRange, string? mode, string? commandName, string? userName, int limit)
    {
        _dayRange    = Math.Min(Math.Max(dayRange, 1), 90);
        _mode        = mode;
        _commandName = commandName;
        _userName    = userName;
        _limit       = Math.Min(Math.Max(limit, 1), 200);
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIManageRevit", "Logs", "bimanage.db");

            if (!File.Exists(dbPath))
            {
                Result = new AuditLogQueryResult
                {
                    Success = false,
                    ErrorMessage = "Audit database not found. ZeManage may not have logged any protection events yet.",
                    Rows = new List<AuditLogRow>()
                };
                return;
            }

            var rows = new List<AuditLogRow>();
            var cutoff = DateTime.Now.AddDays(-_dayRange);

            var sql = @"
                SELECT timestamp, user_name, mode, action, command_name,
                       element_category, element_name, element_count, reason, override_method
                FROM audit_log
                WHERE timestamp >= @cutoff";

            if (!string.IsNullOrEmpty(_mode))         sql += " AND mode = @mode";
            if (!string.IsNullOrEmpty(_commandName))  sql += " AND command_name LIKE @cmd";
            if (!string.IsNullOrEmpty(_userName))     sql += " AND user_name LIKE @user";

            sql += " ORDER BY timestamp DESC LIMIT @limit";

            var cs = SqliteConnectionHelper.BuildConnectionString(dbPath);
            using var conn = new SQLiteConnection(cs);
            conn.Open();
            using var cmd = new SQLiteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
            if (!string.IsNullOrEmpty(_mode))        cmd.Parameters.AddWithValue("@mode", _mode);
            if (!string.IsNullOrEmpty(_commandName)) cmd.Parameters.AddWithValue("@cmd",  $"%{_commandName}%");
            if (!string.IsNullOrEmpty(_userName))    cmd.Parameters.AddWithValue("@user", $"%{_userName}%");
            cmd.Parameters.AddWithValue("@limit", _limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new AuditLogRow
                {
                    Timestamp        = reader.IsDBNull(0) ? null : reader.GetString(0),
                    UserName         = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Mode             = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Action           = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CommandName      = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ElementCategory  = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ElementName      = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ElementCount     = reader.IsDBNull(7) ? 0    : reader.GetInt32(7),
                    Reason           = reader.IsDBNull(8) ? null : reader.GetString(8),
                    OverrideMethod   = reader.IsDBNull(9) ? null : reader.GetString(9)
                });
            }

            Result = new AuditLogQueryResult
            {
                Success = true,
                TotalRowsReturned = rows.Count,
                DayRangeSearched = _dayRange,
                FiltersApplied = BuildFilterSummary(),
                Rows = rows
            };
        }
        catch (Exception ex)
        {
            Result = new AuditLogQueryResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Rows = new List<AuditLogRow>()
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    private string BuildFilterSummary()
    {
        var parts = new List<string> { $"last {_dayRange} day(s)" };
        if (!string.IsNullOrEmpty(_mode))        parts.Add($"mode={_mode}");
        if (!string.IsNullOrEmpty(_commandName)) parts.Add($"command~={_commandName}");
        if (!string.IsNullOrEmpty(_userName))    parts.Add($"user~={_userName}");
        return string.Join(", ", parts);
    }

    public string GetName() => "ZeManage Query Audit Log";
}

public sealed class AuditLogQueryResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int TotalRowsReturned { get; set; }
    public int DayRangeSearched { get; set; }
    public string? FiltersApplied { get; set; }
    public List<AuditLogRow> Rows { get; set; } = new();
}

public sealed class AuditLogRow
{
    public string? Timestamp { get; set; }
    public string? UserName { get; set; }
    public string? Mode { get; set; }
    public string? Action { get; set; }
    public string? CommandName { get; set; }
    public string? ElementCategory { get; set; }
    public string? ElementName { get; set; }
    public int ElementCount { get; set; }
    public string? Reason { get; set; }
    public string? OverrideMethod { get; set; }
}
