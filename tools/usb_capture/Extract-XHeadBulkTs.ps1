param(
    [Parameter(Mandatory = $true)]
    [string]$CapturePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [int]$DeviceAddress = 0
)

$ErrorActionPreference = 'Stop'

$tshark = Join-Path $env:ProgramFiles 'Wireshark\tshark.exe'
if (-not (Test-Path -LiteralPath $tshark)) {
    throw "tshark.exe was not found: $tshark"
}

$capture = (Resolve-Path -LiteralPath $CapturePath).Path
$deviceFilter = if ($DeviceAddress -gt 0) {
    "usb.device_address == $DeviceAddress && "
} else {
    ''
}
$displayFilter = "${deviceFilter}usb.endpoint_address == 0x01 && usb.data_len == 24064"

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFullPath)
if ($outputDirectory) {
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

$stream = [IO.File]::Open(
    $outputFullPath,
    [IO.FileMode]::Create,
    [IO.FileAccess]::Write,
    [IO.FileShare]::Read
)

$sliceCount = 0
try {
    & $tshark -r $capture -Y $displayFilter -T fields -e usb.capdata |
        ForEach-Object {
            $hex = $_.Trim()
            if ($hex.Length -ne 48128) {
                return
            }

            $slice = [byte[]]::new(24064)
            for ($offset = 0; $offset -lt $slice.Length; $offset += 4) {
                $hexOffset = $offset * 2
                $slice[$offset]     = [Convert]::ToByte($hex.Substring($hexOffset + 6, 2), 16)
                $slice[$offset + 1] = [Convert]::ToByte($hex.Substring($hexOffset + 4, 2), 16)
                $slice[$offset + 2] = [Convert]::ToByte($hex.Substring($hexOffset + 2, 2), 16)
                $slice[$offset + 3] = [Convert]::ToByte($hex.Substring($hexOffset, 2), 16)
            }

            $stream.Write($slice, 0, $slice.Length)
            $sliceCount++
        }
}
finally {
    $stream.Dispose()
}

if ($sliceCount -eq 0) {
    throw 'No 24064-byte XHEAD bulk OUT slices were found.'
}

$packetCount = $sliceCount * 128
Write-Output "Extracted $sliceCount slices / $packetCount TS packets to $outputFullPath"
