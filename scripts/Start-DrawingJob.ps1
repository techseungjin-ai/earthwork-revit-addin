param([Parameter(Mandatory=$true)][string]$AssemblyDirectory,[string]$OutputDirectory,[string]$InputFile,[switch]$Horizontal)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=if($OutputDirectory){$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)}else{Join-Path $repo 'local-data/drawing261019'}
if($Horizontal){if(!$InputFile){throw '수평 작업에는 수직부재 완료 RVT 경로가 필요합니다.'};if(Test-Path -LiteralPath $output){throw '원본과 이전 결과 보호를 위해 새 출력 폴더를 지정하세요.'};New-Item -ItemType Directory -Path $output | Out-Null;Copy-Item -LiteralPath $InputFile -Destination (Join-Path $output '프로젝트-복사본.rvt')}
$config=Join-Path $output 'job-config.json'
@{Copy=(Join-Path $output '프로젝트-복사본.rvt');Output=$output;Horizontal=[bool]$Horizontal}|ConvertTo-Json|Set-Content -LiteralPath $config -Encoding utf8
$addins=Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2027'
$assembly=Join-Path (Resolve-Path -LiteralPath $AssemblyDirectory).Path 'CivilBim.Revit.V04.dll'
$dll=[Security.SecurityElement]::Escape($assembly)
$manifest=Join-Path $addins 'CivilBIM.DrawingJob.addin'
Set-Content -LiteralPath $manifest -Encoding utf8 -Value "<?xml version=`"1.0`" encoding=`"utf-8`"?><RevitAddIns><AddIn Type=`"Application`"><Name>CivilBIM Drawing Job</Name><Assembly>$dll</Assembly><AddInId>21D88C3A-467F-4EEC-9922-D1177BB9D5A0</AddInId><FullClassName>CivilBim.Revit.DrawingModelStartup</FullClassName><VendorId>CBIM</VendorId></AddIn></RevitAddIns>"
Copy-Item -LiteralPath (Join-Path $addins 'CivilBIM.addin') -Destination (Join-Path $output 'previous-CivilBIM.addin.disabled')
$xml=[xml](Get-Content -LiteralPath (Join-Path $addins 'CivilBIM.addin') -Raw);$xml.RevitAddIns.AddIn.Assembly=$assembly;$xml.Save((Join-Path $addins 'CivilBIM.addin'))
$previous=$env:CIVILBIM_DRAWING_JOB
try{$env:CIVILBIM_DRAWING_JOB=$config;$p=Start-Process -FilePath 'C:/Program Files/Autodesk/Revit 2027/Revit.exe' -WindowStyle Hidden -PassThru;"Drawing job PID $($p.Id)"}finally{$env:CIVILBIM_DRAWING_JOB=$previous}
