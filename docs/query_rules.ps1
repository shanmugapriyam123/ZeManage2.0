$db = "C:\Users\PC\AppData\Local\BIManageRevit\Logs\bimanage.db"
$query = @"
SELECT r.rule_id, r.name, r.mode,
       GROUP_CONCAT(rc.command_id) AS command_ids,
       GROUP_CONCAT(rc.command_name) AS command_names
FROM rules r
LEFT JOIN rule_commands rc ON r.rule_id = rc.rule_id
WHERE r.is_enabled = 1
GROUP BY r.rule_id, r.name, r.mode
"@

# Load SQLite DLL
$sqliteDll = "C:\Users\PC\Documents\GitHub_JC\BIManageRevit\bin\Debug R24\System.Data.SQLite.dll"
[System.Reflection.Assembly]::LoadFrom($sqliteDll) | Out-Null

$conn = New-Object System.Data.SQLite.SQLiteConnection("Data Source=$db")
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = $query
$reader = $cmd.ExecuteReader()

while($reader.Read()) {
    Write-Host "RuleId: $($reader['rule_id'])"
    Write-Host "Name: $($reader['name'])"
    Write-Host "Mode: $($reader['mode'])"
    Write-Host "CommandIds: $($reader['command_ids'])"
    Write-Host "CommandNames: $($reader['command_names'])"
    Write-Host "---"
}

$reader.Close()
$conn.Close()
