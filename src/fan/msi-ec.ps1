# MSI laptop EC access through the BIOS ACPI-WMI interface (root\wmi : MSI_ACPI Get_Data / Set_Data).
# No drivers. Requires an elevated process. Board: MSI Thin GF63 12VE, EC fw 16R8IMS1.117 (msi-ec CONF_G2_6).
# Dot-source:  . <root>\fan-tray\msi-ec.ps1

$script:MsiAcpi = $null
$EcMap = [ordered]@{
    CpuTemp      = 0x68   # live CPU temperature, deg C
    CpuFanPct    = 0x71   # live fan duty, %
    UpTemps      = 0x6A   # 6 bytes: thresholds (deg C), ascending
    Speeds       = 0x72   # 7 bytes: speed below T1, T1-T2, ..., above T6 (%; 0-150, 100 = fan STOPS on this EC)
    DownOffsets  = 0x7A   # 6 bytes: hysteresis, deg C below each threshold
    RpmRaw       = 0xC8   # 2 bytes big-endian, RPM = 478000 / raw
    CoolerBoost  = 0x98   # bit 7
    ShiftMode    = 0xD2   # 0xC4 turbo (Extreme), 0xC1 comfort (Balanced), 0xC2 eco (Silent)
    FanMode      = 0xD4   # 0x0D auto, 0x1D silent, 0x4D basic, 0x8D advanced (curve)
    SuperBattery = 0xEB   # low nibble 0x0F = super battery (eco throttle), 0x00 = off; high nibble left untouched
    Charge       = 0xD7   # 0x80 + limit %, 0x80 = no limit (read only here; owner: don't set)
}
$ShiftModes = @{ C4 = 'Extreme'; C1 = 'Balanced'; C2 = 'Eco' }

function Get-MsiAcpi {
    if (-not $script:MsiAcpi) {
        $script:MsiAcpi = Get-CimInstance -Namespace root\wmi -ClassName MSI_ACPI -ErrorAction Stop
    }
    $script:MsiAcpi
}

function New-EcPackage([byte]$a, [byte]$v = 0) {
    $b = New-Object byte[] 32; $b[0] = $a; $b[1] = $v
    New-CimInstance -Namespace root\wmi -ClassName Package_32 -ClientOnly -Property @{ Bytes = $b }
}

function Read-EcByte([byte]$Address) {
    $r = Invoke-CimMethod -InputObject (Get-MsiAcpi) -MethodName Get_Data -Arguments @{ Data = (New-EcPackage $Address) }
    if ($r.Data.Bytes[0] -ne 1) { throw ("EC read failed at 0x{0:X2}" -f $Address) }
    [int]$r.Data.Bytes[1]
}

function Read-EcBytes([byte]$Address, [int]$Count) { 0..($Count - 1) | ForEach-Object { Read-EcByte ($Address + $_) } }

# Writes one byte and verifies it by reading it back.
function Write-EcByte([byte]$Address, [byte]$Value) {
    $r = Invoke-CimMethod -InputObject (Get-MsiAcpi) -MethodName Set_Data -Arguments @{ Data = (New-EcPackage $Address $Value) }
    if ($r.Data.Bytes[0] -ne 1) { throw ("EC write failed at 0x{0:X2}" -f $Address) }
    $back = Read-EcByte $Address
    if ($back -ne $Value) { throw ("EC read-back mismatch at 0x{0:X2}: wrote {1}, read {2}" -f $Address, $Value, $back) }
}

function Read-FanState {
    $raw = (Read-EcByte $EcMap.RpmRaw) * 256 + (Read-EcByte ($EcMap.RpmRaw + 1))
    $charge = Read-EcByte $EcMap.Charge
    [pscustomobject]@{
        CpuTemp     = Read-EcByte $EcMap.CpuTemp
        FanPct      = Read-EcByte $EcMap.CpuFanPct
        Rpm         = if ($raw -gt 0) { [int](478000 / $raw) } else { 0 }
        UpTemps     = @(Read-EcBytes $EcMap.UpTemps 6)
        DownOffsets = @(Read-EcBytes $EcMap.DownOffsets 6)
        Speeds      = @(Read-EcBytes $EcMap.Speeds 7)
        FanMode     = '0x{0:X2}' -f (Read-EcByte $EcMap.FanMode)
        ShiftMode   = '0x{0:X2}' -f (Read-EcByte $EcMap.ShiftMode)
        SuperBattery = ((Read-EcByte $EcMap.SuperBattery) -band 0x0F) -eq 0x0F
        CoolerBoost = [bool]((Read-EcByte $EcMap.CoolerBoost) -band 0x80)
        ChargeLimit = if ($charge -band 0x80 -and ($charge -band 0x7F) -gt 0) { $charge -band 0x7F } else { $null }
    }
}

# Full 256-byte snapshot as hex text (backup / diagnostics).
function Backup-Ec([string]$File) {
    $ec = 0..255 | ForEach-Object { Read-EcByte $_ }
    $lines = @("# EC dump $(Get-Date -f 'yyyy-MM-dd HH:mm:ss') via MSI_ACPI.Get_Data")
    for ($r = 0; $r -lt 16; $r++) { $lines += ('{0:X2}0: ' -f $r) + (($ec[($r * 16)..($r * 16 + 15)] | ForEach-Object { '{0:X2}' -f $_ }) -join ' ') }
    $tmp = "$File.tmp"; $lines | Set-Content -Encoding utf8 $tmp; Move-Item $tmp $File -Force
    $File
}

# Validates a fan curve (also used for EC dumps by restore); throws with a clear reason.
function Test-FanCurve($t, $d, $s) {
    $t = @($t); $d = @($d); $s = @($s)
    # Whole numbers only: [byte] rounds, so 99.6 or 100.4 would be written as exactly 100 (fan stops).
    foreach ($v in $t + $d + $s) { if ($v -isnot [ValueType] -or [double]$v -ne [math]::Floor([double]$v)) { throw "curve value '$v' must be a whole number" } }
    if ($t.Count -ne 6) { throw 'upTemps needs 6 values' }
    if ($d.Count -ne 6) { throw 'downOffsets needs 6 values' }
    if ($s.Count -ne 7) { throw 'speeds needs 7 values (below T1 ... above T6)' }
    for ($i = 0; $i -lt 6; $i++) {
        if ($t[$i] -lt 40 -or $t[$i] -gt 95) { throw "upTemps[$i]=$($t[$i]) outside 40-95" }
        if ($i -gt 0 -and $t[$i] -le $t[$i - 1]) { throw 'upTemps must be strictly ascending' }
        if ($d[$i] -lt 1 -or $d[$i] -gt 10) { throw "downOffsets[$i]=$($d[$i]) outside 1-10" }
    }
    foreach ($v in $s) {
        if ($v -lt 0 -or $v -gt 150) { throw "speed $v outside 0-150" }
        if ($v -eq 100) { throw 'speed 100 is not allowed: this EC stops the fan at exactly 100 (use 99 or 101+)' }
    }
    if ($s[6] -lt 60) { throw 'speed above the top threshold must be >= 60' }
    if ($s[5] -lt 40) { throw 'speed in the second-highest band must be >= 40' }
}

# Validates a profile object (from fan-profile.json); throws with a clear reason.
function Test-FanProfile($p) {
    Test-FanCurve $p.upTemps $p.downOffsets $p.speeds
    if (-not $ShiftModes.ContainsKey([string]$p.shiftMode)) { throw "shiftMode must be one of: $($ShiftModes.Keys -join ', ')" }
    if ($p.superBattery -isnot [bool]) { throw 'superBattery must be true or false' }
    $true
}

# Writes a validated profile; returns the list of bytes that actually changed.
function Write-FanProfile($p) {
    [void](Test-FanProfile $p)
    # (address, value) pairs in write order; the fan mode goes last, after the table is complete.
    $plan = New-Object System.Collections.Generic.List[int[]]
    for ($i = 0; $i -lt 6; $i++) { $plan.Add(@(($EcMap.UpTemps + $i), $p.upTemps[$i])); $plan.Add(@(($EcMap.DownOffsets + $i), $p.downOffsets[$i])) }
    for ($i = 0; $i -lt 7; $i++) { $plan.Add(@(($EcMap.Speeds + $i), $p.speeds[$i])) }
    # Mode recipe (GhostDeck models.json, 16R8IMS1): shift byte + super-battery nibble, then the curve fan mode.
    $plan.Add(@($EcMap.ShiftMode, [Convert]::ToInt32($p.shiftMode, 16)))
    $eb = Read-EcByte $EcMap.SuperBattery
    $plan.Add(@($EcMap.SuperBattery, (($eb -band 0xF0) -bor $(if ($p.superBattery) { 0x0F } else { 0x00 }))))
    $plan.Add(@($EcMap.FanMode, 0x8D))   # advanced = use the curve
    $changed = @()
    foreach ($pair in $plan) {
        $a = $pair[0]; $v = $pair[1]
        $cur = Read-EcByte $a
        if ($cur -ne $v) { Write-EcByte $a $v; $changed += ('0x{0:X2}: {1} -> {2}' -f $a, $cur, $v) }
    }
    , $changed
}
