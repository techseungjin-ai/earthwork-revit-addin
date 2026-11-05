param([Parameter(Mandatory=$true)][string]$AssemblyDirectory,[string]$Run='Local-A')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repo ('local-data\local-workflow\'+$Run)
New-Item -ItemType Directory -Path $output -Force | Out-Null
'{"Status":"starting"}' | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
$installed=& (Join-Path $PSScriptRoot 'Apply-Local.ps1') -AssemblyDirectory $AssemblyDirectory
$addins=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027'
foreach($name in @('CivilBIM.RevisionTest.addin','CivilBIM.ModelessTest.addin')){$old=Join-Path $addins $name;if(Test-Path -LiteralPath $old){Remove-Item -LiteralPath $old}}
$dll=[Security.SecurityElement]::Escape((Join-Path $installed 'CivilBim.Revit.V04.dll'))
Set-Content -LiteralPath (Join-Path $addins 'CivilBIM.LocalWorkflowTest.addin') -Encoding utf8 -Value "<?xml version=`"1.0`" encoding=`"utf-8`"?><RevitAddIns><AddIn Type=`"Application`"><Name>CivilBIM Local Workflow Test</Name><Assembly>$dll</Assembly><AddInId>E5E59F62-061F-4808-9D5D-773516F9CA41</AddInId><FullClassName>CivilBim.Revit.LocalWorkflowCheckStartup</FullClassName><VendorId>CBIM</VendorId></AddIn></RevitAddIns>"
$previous=$env:CIVILBIM_LOCAL_CHECKS
try{$env:CIVILBIM_LOCAL_CHECKS=$output;$process=Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2027\Revit.exe' -WindowStyle Hidden -PassThru;Write-Output "Local workflow test PID $($process.Id)";Write-Output "Installed test DLL: $installed"}finally{$env:CIVILBIM_LOCAL_CHECKS=$previous}
