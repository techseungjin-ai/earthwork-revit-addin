param([Parameter(Mandatory=$true)][string]$SampleFolder,[Parameter(Mandatory=$true)][string]$Pdf)
$ErrorActionPreference='Stop'
if(Get-Process Revit -ErrorAction SilentlyContinue){throw '사용자 문서와 분리된 검증을 위해 Revit을 닫은 상태에서 실행하세요.'}
$repo=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repo 'local-data\revit-smoke'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$config=Join-Path $output 'config.json'
@{OutputFolder=$output; SampleFolder=(Resolve-Path -LiteralPath $SampleFolder).Path; Pdf=(Resolve-Path -LiteralPath $Pdf).Path; DrawingExtract=(Join-Path $repo 'tmp\drawing-extract.txt')} | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
$reportPath=Join-Path $output 'revit-smoke.json'
if(Test-Path -LiteralPath $reportPath){Copy-Item -LiteralPath $reportPath -Destination (Join-Path $output ('revit-smoke-previous-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'.json'))}
'{"Status":"starting"}' | Set-Content -LiteralPath $reportPath -Encoding utf8
$previous=$env:CIVILBIM_SMOKE_CONFIG
try {
    $env:CIVILBIM_SMOKE_CONFIG=$config
    $process=Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2027\Revit.exe' -WindowStyle Hidden -PassThru
    Write-Output "Revit test process: $($process.Id); report: $(Join-Path $output 'revit-smoke.json')"
} finally {$env:CIVILBIM_SMOKE_CONFIG=$previous}
