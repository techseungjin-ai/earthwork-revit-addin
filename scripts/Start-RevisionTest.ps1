param([string]$Folder='C:\Users\최승진\Desktop\샘플',[string]$AssemblyDirectory,[string]$Run='R2',[double]$RotationDegrees=0)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repo ('local-data\revision\'+$Run)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$config=Join-Path $output 'config.json'
@{Folder=$Folder; Output=$output; Pdf='C:\Users\최승진\Downloads\report.pdf';RotationDegrees=$RotationDegrees} | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
'{"Status":"starting"}' | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
if(!$AssemblyDirectory){$AssemblyDirectory=Join-Path $repo 'src\CivilBim.Revit\bin\Release\net10.0-windows'}
$assembly=Join-Path $AssemblyDirectory 'CivilBim.Revit.V04.dll'
if(!(Test-Path -LiteralPath $assembly)){throw '성공한 0.4 빌드 DLL을 먼저 지정하세요.'}
$dll=[Security.SecurityElement]::Escape($assembly)
$otherTest=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027\CivilBIM.ModelessTest.addin'
if(Test-Path -LiteralPath $otherTest){Remove-Item -LiteralPath $otherTest}
$manifest=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027\CivilBIM.RevisionTest.addin'
Set-Content -LiteralPath $manifest -Encoding utf8 -Value "<?xml version=`"1.0`" encoding=`"utf-8`"?><RevitAddIns><AddIn Type=`"Application`"><Name>CivilBIM Revision Test</Name><Assembly>$dll</Assembly><AddInId>B4BF2A75-C403-42FC-91BE-FAF2FE8F09AC</AddInId><FullClassName>CivilBim.Revit.RevisionStartup</FullClassName><VendorId>CBIM</VendorId></AddIn></RevitAddIns>"
$previous=$env:CIVILBIM_REVISION_CONFIG
try{$env:CIVILBIM_REVISION_CONFIG=$config;$p=Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2027\Revit.exe' -WindowStyle Hidden -PassThru;Write-Output "Revision host PID $($p.Id)"}finally{$env:CIVILBIM_REVISION_CONFIG=$previous}

