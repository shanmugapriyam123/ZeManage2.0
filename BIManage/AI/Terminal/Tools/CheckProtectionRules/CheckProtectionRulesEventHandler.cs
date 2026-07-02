using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.Data.SQLite;

namespace BIManage.AI.Terminal.Tools.CheckProtectionRules;

/// <summary>
/// Handler for <c>check_protection_rules</c>. Reads <c>command_settings</c> rows from the
/// local SQLite DB, joined with <c>protection_settings</c> for the protection name.
/// </summary>
internal sealed class CheckProtectionRulesEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);

    private string? _modelGuid;
    private string? _commandCode;
    private bool _enabledOnly = true;

    public ProtectionRulesResult? Result { get; private set; }

    public void SetParameters(string? modelGuid, string? commandCode, bool enabledOnly)
    {
        _modelGuid   = modelGuid;
        _commandCode = commandCode;
        _enabledOnly = enabledOnly;
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
                Result = new ProtectionRulesResult
                {
                    Success = false,
                    ErrorMessage = "Protection settings database not found.",
                    Rules = new List<ProtectionRuleRow>()
                };
                return;
            }

            var rows = new List<ProtectionRuleRow>();

            // scope: 0=Company, 1=Project, 2=Model. When modelGuid is supplied, return everything
            // that COULD apply to this model: company/project-scoped rules (no model filter) plus
            // model-scoped rules matching this GUID.
            var sql = @"
                SELECT cs.command_code, cs.scope, cs.is_enabled, cs.intervention_mode,
                       cs.model_guid, cs.project_id, cs.company_id, cs.allow_admin_override,
                       cs.require_comment, cs.send_email
                FROM command_settings cs
                WHERE 1=1";

            if (_enabledOnly)                       sql += " AND cs.is_enabled = 1";
            if (!string.IsNullOrEmpty(_commandCode)) sql += " AND cs.command_code LIKE @cmd";
            if (!string.IsNullOrEmpty(_modelGuid))
                sql += " AND (cs.scope IN (0, 1) OR (cs.scope = 2 AND cs.model_guid = @model))";

            sql += " ORDER BY cs.scope ASC, cs.command_code ASC";

            var cs = SqliteConnectionHelper.BuildConnectionString(dbPath);
            using var conn = new SQLiteConnection(cs);
            conn.Open();
            using var cmd = new SQLiteCommand(sql, conn);
            if (!string.IsNullOrEmpty(_commandCode)) cmd.Parameters.AddWithValue("@cmd",   $"%{_commandCode}%");
            if (!string.IsNullOrEmpty(_modelGuid))   cmd.Parameters.AddWithValue("@model", _modelGuid);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ProtectionRuleRow
                {
                    CommandCode        = reader.IsDBNull(0) ? null : reader.GetString(0),
                    Scope              = reader.IsDBNull(1) ? "(unknown)" : ScopeName(reader.GetInt32(1)),
                    IsEnabled          = !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
                    Mode               = reader.IsDBNull(3) ? "(unknown)" : ModeName(reader.GetInt32(3)),
                    ModelGuid          = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ProjectId          = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CompanyId          = reader.IsDBNull(6) ? null : reader.GetString(6),
                    AllowAdminOverride = !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
                    RequireComment     = !reader.IsDBNull(8) && reader.GetInt32(8) == 1,
                    SendEmail          = !reader.IsDBNull(9) && reader.GetInt32(9) == 1
                });
            }

            Result = new ProtectionRulesResult
            {
                Success = true,
                TotalRulesReturned = rows.Count,
                FiltersApplied = BuildFilterSummary(),
                Rules = rows
            };
        }
        catch (Exception ex)
        {
            Result = new ProtectionRulesResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Rules = new List<ProtectionRuleRow>()
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    private static string ScopeName(int scope) => scope switch
    {
        0 => "Company",
        1 => "Project",
        2 => "Model",
        _ => $"Unknown({scope})"
    };

    // intervention_mode mapping mirrors BIManage.Core.Protection.Models.ProtectionMode order.
    // 0=Notify (green) → 1=Assist (yellow) → 2=Protect (red).
    private static string ModeName(int mode) => mode switch
    {
        0 => "Notify",
        1 => "Assist",
        2 => "Protect",
        _ => $"Unknown({mode})"
    };

    private string BuildFilterSummary()
    {
        var parts = new List<string>();
        if (_enabledOnly)                        parts.Add("enabled only");
        if (!string.IsNullOrEmpty(_modelGuid))   parts.Add("scoped to current model");
        if (!string.IsNullOrEmpty(_commandCode)) parts.Add($"command~={_commandCode}");
        return parts.Count == 0 ? "no filters (all rules)" : string.Join(", ", parts);
    }

    public string GetName() => "ZeManage Check Protection Rules";
}

public sealed class ProtectionRulesResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int TotalRulesReturned { get; set; }
    public string? FiltersApplied { get; set; }
    public List<ProtectionRuleRow> Rules { get; set; } = new();
}

public sealed class ProtectionRuleRow
{
    public string? CommandCode { get; set; }
    public string Scope { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string Mode { get; set; } = "";
    public string? ModelGuid { get; set; }
    public string? ProjectId { get; set; }
    public string? CompanyId { get; set; }
    public bool AllowAdminOverride { get; set; }
    public bool RequireComment { get; set; }
    public bool SendEmail { get; set; }
}
