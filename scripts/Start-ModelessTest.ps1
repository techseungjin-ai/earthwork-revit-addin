param([string]$AssemblyDirectory,[string]$Run='UI04-A')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repo ('local-data\modeless\'+$Run)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$config=Join-Path $output 'config.json'
@{Output=$output;Pdf='C:\Users\최승진\Downloads\report.pdf'} | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
'{"Status":"starting"}' | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
$assembly=Join-Path $AssemblyDirectory 'CivilBim.Revit.V04.dll'
if(!(Test-Path -LiteralPath $assembly)){throw '성공한 0.4 빌드 DLL을 먼저 지정하세요.'}
$dll=[Security.SecurityElement]::Escape($assembly)
$otherTest=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027\CivilBIM.RevisionTest.addin'
if(Test-Path -LiteralPath $otherTest){Remove-Item -LiteralPath $otherTest}
$manifest=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027\CivilBIM.ModelessTest.addin'
Set-Content -LiteralPath $manifest -Encoding utf8 -Value "<?xml version=`"1.0`" encoding=`"utf-8`"?><RevitAddIns><AddIn Type=`"Application`"><Name>CivilBIM Modeless Test</Name><Assembly>$dll</Assembly><AddInId>7E52AA85-341D-4822-988E-2AD818092C79</AddInId><FullClassName>CivilBim.Revit.ModelessCheckStartup</FullClassName><VendorId>CBIM</VendorId></AddIn></RevitAddIns>"
$previous=$env:CIVILBIM_UI_CONFIG
try{$env:CIVILBIM_UI_CONFIG=$config;$p=Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2027\Revit.exe' -WindowStyle Hidden -PassThru;Write-Output "Modeless test PID $($p.Id)"}finally{$env:CIVILBIM_UI_CONFIG=$previous}
