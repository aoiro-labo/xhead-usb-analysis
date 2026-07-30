param(
    [Parameter(Mandatory = $true)]
    [string]$Capture,

    [int]$DeviceAddress = 40,

    [int]$TransferBytes = 24064
)

$tshark = (Get-Command tshark.exe -ErrorAction SilentlyContinue).Source
if (-not $tshark) {
    $candidate = Join-Path $env:ProgramFiles "Wireshark\tshark.exe"
    if (Test-Path $candidate) { $tshark = $candidate }
}
if (-not $tshark) { throw "tshark.exe was not found." }
if (-not (Test-Path -LiteralPath $Capture)) { throw "Capture not found: $Capture" }

$filter = "usb.device_address == $DeviceAddress && usb.endpoint_address.direction == 0 && " +
    "usb.transfer_type == 0x03 && usb.data_len == $TransferBytes"
$raw = & $tshark -r $Capture -Y $filter -T fields -e frame.time_epoch
if ($LASTEXITCODE -ne 0) { throw "tshark failed with exit code $LASTEXITCODE." }

[double[]]$times = @($raw | ForEach-Object { [double]$_ })
if ($times.Count -lt 2) { throw "Fewer than two matching bulk transfers were found." }

[double[]]$intervals = for ($i = 1; $i -lt $times.Count; $i++) {
    1000.0 * ($times[$i] - $times[$i - 1])
}
$stats = $intervals | Measure-Object -Minimum -Maximum -Average
$elapsed = $times[-1] - $times[0]
$wireBitrate = ($times.Count * $TransferBytes * 8.0) / $elapsed

[pscustomobject]@{
    Capture = (Resolve-Path -LiteralPath $Capture).Path
    DeviceAddress = $DeviceAddress
    Transfers = $times.Count
    TransferBytes = $TransferBytes
    ElapsedSeconds = [math]::Round($elapsed, 6)
    AverageIntervalMs = [math]::Round($stats.Average, 3)
    MinimumIntervalMs = [math]::Round($stats.Minimum, 3)
    MaximumIntervalMs = [math]::Round($stats.Maximum, 3)
    AverageUsbPayloadBitrate = [math]::Round($wireBitrate)
    IntervalsUnder3Ms = @($intervals | Where-Object { $_ -lt 3 }).Count
    Intervals10To30Ms = @($intervals | Where-Object { $_ -ge 10 -and $_ -lt 30 }).Count
    IntervalsAtLeast30Ms = @($intervals | Where-Object { $_ -ge 30 }).Count
}
