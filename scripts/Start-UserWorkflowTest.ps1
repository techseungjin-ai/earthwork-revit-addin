param([Parameter(Mandatory=$true)][string]$AssemblyDirectory,[string]$Run='User-A')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repo ('local-data\user-workflow\'+$Run)
if(Test-Path -LiteralPath $output){throw '새 검증 이름을 사용하세요.'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$source='C:\Users\최승진\Desktop\샘플\test.rvt'
$copy=Join-Path $output '사용자료-복사본.rvt'
$hash=(Get-FileHash -LiteralPath $source).Hash
Copy-Item -LiteralPath $source -Destination $copy
if((Get-FileHash -LiteralPath $copy).Hash -ne $hash){throw '원본/검증복사 해시 불일치'}
$hash | Set-Content -LiteralPath (Join-Path $output 'original-sha256.txt') -Encoding ascii
$boreholes='C:\Users\최승진\AppData\Local\CivilBIM\Boreholes\boreholes-f7bed4624a6f4a9caa9de0ae7bab87b1.json'
$config=Join-Path $output 'config.json'
@{Copy=$copy;Boreholes=$boreholes;Output=$output} | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
$dll=[Security.SecurityElement]::Escape((Join-Path (Resolve-Path -LiteralPath $AssemblyDirectory).Path 'CivilBim.Revit.V04.dll'))
$addins=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027'
$manifest=Join-Path $addins 'CivilBIM.UserWorkflowTest.addin'
Set-Content -LiteralPath $manifest -Encoding utf8 -Value "<?xml version=`"1.0`" encoding=`"utf-8`"?><RevitAddIns><AddIn Type=`"Application`"><Name>CivilBIM User Workflow Test</Name><Assembly>$dll</Assembly><AddInId>B9D2BA31-D039-418E-BA38-C866B85C155A</AddInId><FullClassName>CivilBim.Revit.UserWorkflowCheckStartup</FullClassName><VendorId>CBIM</VendorId></AddIn></RevitAddIns>"
# Both application entries must point to this build: one assembly name cannot be loaded twice.
Copy-Item -LiteralPath (Join-Path $addins 'CivilBIM.addin') -Destination (Join-Path $output 'previous-CivilBIM.addin.disabled')
$xml=[xml](Get-Content -LiteralPath (Join-Path $addins 'CivilBIM.addin') -Raw)
$xml.RevitAddIns.AddIn.Assembly=(Join-Path (Resolve-Path -LiteralPath $AssemblyDirectory).Path 'CivilBim.Revit.V04.dll')
$xml.Save((Join-Path $addins 'CivilBIM.addin'))
$previous=$env:CIVILBIM_USER_CHECKS
try{$env:CIVILBIM_USER_CHECKS=$config;$process=Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2027\Revit.exe' -WindowStyle Hidden -PassThru;Write-Output "User workflow test PID $($process.Id) / $output"}finally{$env:CIVILBIM_USER_CHECKS=$previous}
