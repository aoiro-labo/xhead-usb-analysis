param(
    [ValidateSet('Inspect', 'PassThrough', 'ExtractPid', 'InjectEit')]
    [string]$Mode = 'Inspect',
    [Parameter(Mandatory = $true)]
    [string]$InputTs,
    [string]$Output,
    [string[]]$Pids,
    [string]$Eit,
    [long]$Bitrate = 0,
    [string]$Stuffing = '1/20',
    [string]$Tsp = 'C:\Program Files\TSDuck\bin\tsp.exe'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Tsp)) {
    throw "tsp.exeが見つかりません: $Tsp"
}
if (-not (Test-Path -LiteralPath $InputTs)) {
    throw "入力TSが見つかりません: $InputTs"
}

function Require-Output {
    if ([string]::IsNullOrWhiteSpace($Output)) {
        throw "$Mode には -Output が必要です。"
    }
}

switch ($Mode) {
    'Inspect' {
        & $Tsp --japan -I file $InputTs -P analyze -O drop
        if ($LASTEXITCODE -ne 0) { throw "TS解析に失敗しました: exit=$LASTEXITCODE" }
    }
    'PassThrough' {
        Require-Output
        $argsList = @('--japan', '-I', 'file', $InputTs)
        if ($Bitrate -gt 0) {
            $argsList += @('-P', 'regulate', '--bitrate', $Bitrate)
        }
        $argsList += @('-O', 'file', $Output)
        & $Tsp @argsList
        if ($LASTEXITCODE -ne 0) { throw "TSコピーに失敗しました: exit=$LASTEXITCODE" }
    }
    'ExtractPid' {
        Require-Output
        if ($null -eq $Pids -or $Pids.Count -eq 0) {
            throw 'ExtractPidには-Pids 0x0114[,0x....]が必要です。'
        }
        $argsList = @('--japan', '-I', 'file', $InputTs, '-P', 'filter')
        foreach ($id in $Pids) {
            foreach ($part in $id.Split(',')) {
                if (-not [string]::IsNullOrWhiteSpace($part)) {
                    $argsList += @('--pid', $part.Trim())
                }
            }
        }
        $argsList += @('-O', 'file', $Output)
        & $Tsp @argsList
        if ($LASTEXITCODE -ne 0) { throw "PID抽出に失敗しました: exit=$LASTEXITCODE" }
    }
    'InjectEit' {
        Require-Output
        if ([string]::IsNullOrWhiteSpace($Eit) -or -not (Test-Path -LiteralPath $Eit)) {
            throw 'InjectEitには既存のEIT XML/JSONを-Eitで指定してください。'
        }
        $argsList = @(
            '--japan', '--add-input-stuffing', $Stuffing, '-I', 'file', $InputTs,
            '-P', 'eitinject', '--japan', '--actual', '--wait-first-batch',
            '--time', 'system', '--files', $Eit,
            '--cycle-pf-actual', '1',
            '--cycle-schedule-actual-prime', '1',
            '--cycle-schedule-actual-later', '1'
        )
        if ($Bitrate -gt 0) {
            $argsList += @('--bitrate', $Bitrate)
        }
        $argsList += @('-O', 'file', $Output)
        & $Tsp @argsList
        if ($LASTEXITCODE -ne 0) { throw "EIT注入に失敗しました: exit=$LASTEXITCODE" }
    }
}
