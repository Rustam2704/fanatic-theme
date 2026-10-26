#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Fan control for MSI Thin GF63 12VE without MSI Center (BIOS WMI, no drivers).
.EXAMPLE
  .\msi-fan.ps1 status            # mode, temps, rpm, curve table with temperature bands
  .\msi-fan.ps1 mode eco          # switch to performance | balanced | eco (saved as activeMode) and apply
  .\msi-fan.ps1 apply             # write the active mode of fan-profile.json to the EC (only changed bytes), verify
  .\msi-fan.ps1 check             # like apply, but logs only when something drifted (for the timer task)
  .\msi-fan.ps1 backup            # full EC dump into backups\
  .\msi-fan.ps1 restore <dump>    # write the curve area (0x6A-0x7F) back from a dump
#>
param(
    [Parameter(Position = 0)][ValidateSet('status', 'mode', 'apply', 'check', 'backup', 'restore')][string]$Action = 'status',
    [Parameter(Position = 1)][string]$File
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\msi-ec.ps1"
$ProfileFile = "$PSScriptRoot\fan-profile.json"
$Log = "$PSScriptRoot\msi-fan.log"
$Backups = "$PSScriptRoot\backups"

function Write-Log([string]$m) {
    $line = "$(Get-Date -f 'yyyy-MM-dd HH:mm:ss') $m"
    # Logging must never fail a run whose EC write already happened (e.g. log open in an editor).
    try {
        Add-Content -Encoding utf8 $Log $line
        # Keep the log small.
        if ((Get-Item $Log).Length -gt 512KB) { Get-Content $Log -Tail 2000 | Set-Content -Encoding utf8 "$Log.tmp"; Move-Item "$Log.tmp" $Log -Force }
    } catch { }
}

function Get-Config { Get-Content $ProfileFile -Raw | ConvertFrom-Json }
# The active mode's settings (shift, super battery, curve).
function Get-Profile {
    $c = Get-Config
    $m = $c.modes.($c.activeMode)
    if (-not $m) { throw "activeMode '$($c.activeMode)' is not defined in fan-profile.json" }
    $m
}

# One EC writer at a time across all tasks (check timer, mode tasks, boot/logon/resume).
# Everyone may open it: SYSTEM tasks and the elevated owner share the same lock.
$MutexSec = New-Object Security.AccessControl.MutexSecurity
$MutexSec.AddAccessRule((New-Object Security.AccessControl.MutexAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-1-0'), 'FullControl', 'Allow')))
$created = $false
$Mutex = New-Object Threading.Mutex($false, 'Global\fanatic.MsiFan', [ref]$created, $MutexSec)
if ($Action -ne 'status') {
    try { if (-not $Mutex.WaitOne(30000)) { throw 'another msi-fan run holds the lock' } }
    catch [Threading.AbandonedMutexException] { }   # previous holder died: we own it now
}
try {
switch ($Action) {
    'status' {
        $s = Read-FanState
        $shift = $ShiftModes[$s.ShiftMode.Substring(2)]
        "mode {0} (EC: shift {1} = {2}, super battery {3}, fan mode {4})" -f (Get-Config).activeMode, $s.ShiftMode, $shift, $s.SuperBattery, $s.FanMode
        "CPU {0} C   fan {1}% = {2} rpm   cooler boost {3}   charge limit {4}" -f $s.CpuTemp, $s.FanPct, $s.Rpm, $s.CoolerBoost, $(if ($s.ChargeLimit) { "$($s.ChargeLimit)%" } else { 'none' })
        $t = $s.UpTemps
        $bands = @("< $($t[0])") + (1..5 | ForEach-Object { "$($t[$_-1])-$($t[$_])" }) + @("> $($t[5])")
        for ($i = 0; $i -lt 7; $i++) {
            $mark = if (($i -eq 0 -and $s.CpuTemp -lt $t[0]) -or ($i -eq 6 -and $s.CpuTemp -ge $t[5]) -or ($i -gt 0 -and $i -lt 6 -and $s.CpuTemp -ge $t[$i - 1] -and $s.CpuTemp -lt $t[$i])) { '  <- now' } else { '' }
            $down = if ($i -gt 0) { "  (down at {0} C)" -f ($t[$i - 1] - $s.DownOffsets[$i - 1]) } else { '' }
            "  {0,-8} C : {1,3}%{2}{3}" -f $bands[$i], $s.Speeds[$i], $down, $mark
        }
    }
    'mode' {
        $c = Get-Config
        $names = @($c.modes.PSObject.Properties.Name)
        $File = $names | Where-Object { $_ -eq $File } | Select-Object -First 1   # case-insensitive, canonical spelling
        if (-not $File) { throw "usage: mode <$($names -join '|')>" }
        [void](Test-FanProfile $c.modes.$File)
        # EC first, config second: if the EC write fails, the saved mode stays what the EC really runs.
        $changed = Write-FanProfile $c.modes.$File
        # Change only the activeMode value so the hand-formatted file stays readable;
        # write the content in place (keeps the file's ACL: the owner may edit it without elevation).
        $raw = (Get-Content $ProfileFile -Raw) -replace '("activeMode"\s*:\s*)"[^"]*"', "`$1`"$File`""
        [IO.File]::WriteAllText($ProfileFile, $raw, (New-Object Text.UTF8Encoding $false))
        Write-Log "mode $File : $($changed -join '; ')"
        "mode $File applied ($($changed.Count) bytes changed)"
    }
    'apply' {
        New-Item -ItemType Directory -Force $Backups | Out-Null
        $bk = Backup-Ec "$Backups\ec-before-apply-$(Get-Date -f 'yyyyMMdd-HHmmss').txt"
        $changed = Write-FanProfile (Get-Profile)
        if ($changed.Count) { Write-Log "apply: $($changed -join '; ') (backup $bk)"; "changed: $($changed -join '; ')" } else { Remove-Item $bk; 'EC already matches the profile' }
        # Keep the last 20 backups.
        Get-ChildItem "$Backups\ec-before-apply-*.txt" | Sort-Object Name -Descending | Select-Object -Skip 20 | Remove-Item
    }
    'check' {
        try {
            $changed = Write-FanProfile (Get-Profile)
            if ($changed.Count) { Write-Log "drift fixed: $($changed -join '; ')" }
        } catch { Write-Log "check ERROR: $($_.Exception.Message)"; throw }
    }
    'backup' {
        New-Item -ItemType Directory -Force $Backups | Out-Null
        Backup-Ec "$Backups\ec-$(Get-Date -f 'yyyyMMdd-HHmmss').txt"
    }
    'restore' {
        if (-not $File) { throw 'restore needs a dump file' }
        $rows = Get-Content $File | Where-Object { $_ -match '^[0-9A-F]{2}0: ' }
        $ec = @(foreach ($r in $rows) { ($r -split ': ')[1] -split ' ' | ForEach-Object { [Convert]::ToInt32($_, 16) } })
        if ($ec.Count -ne 256) { throw "dump must have 256 bytes, has $($ec.Count)" }
        # Same rules as a profile (no 100 %, ascending temps, sane top bands) before anything is written.
        try { Test-FanCurve $ec[0x6A..0x6F] $ec[0x7A..0x7F] $ec[0x72..0x78] } catch { throw "dump curve rejected: $($_.Exception.Message)" }
        $n = 0
        foreach ($a in 0x6A..0x7F) { if ($a -eq 0x70 -or $a -eq 0x71 -or $a -eq 0x79) { continue }; if ((Read-EcByte $a) -ne $ec[$a]) { Write-EcByte $a $ec[$a]; $n++ } }
        Write-Log "restore from $File : $n bytes"; "restored $n bytes"
    }
}
} finally { if ($Action -ne 'status') { $Mutex.ReleaseMutex() } }

