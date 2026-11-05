param([Parameter(Mandatory=$true)][string]$AssemblyDirectory)
$ErrorActionPreference='Stop'
$source=(Resolve-Path -LiteralPath $AssemblyDirectory).Path
if(!(Test-Path -LiteralPath (Join-Path $source 'CivilBim.Revit.V04.dll'))){throw '성공한 0.4 로컬 빌드가 필요합니다.'}
$addins=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027'
$target=Join-Path $addins ('CivilBIM\0.4.0-local\'+[DateTime]::Now.ToString('yyyyMMddHHmmssfff'))
New-Item -ItemType Directory -Path $target -Force | Out-Null
foreach($file in (Get-ChildItem -LiteralPath $source -File | Where-Object {$_.Extension -in '.dll','.json','.pdb'})){
    if($file.Name -match '^RevitAPI(UI)?\.dll$'){throw 'Autodesk API DLL은 설치하지 않습니다.'}
    $copy=Join-Path $target $file.Name;Copy-Item -LiteralPath $file.FullName -Destination $copy
    if((Get-FileHash -LiteralPath $copy).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw '로컬 설치 해시 오류'}
}
$manifest=Join-Path $addins 'CivilBIM.addin'
if(Test-Path -LiteralPath $manifest){Copy-Item -LiteralPath $manifest -Destination (Join-Path $target 'previous-CivilBIM.addin.disabled')}
$dll=[Security.SecurityElement]::Escape((Join-Path $target 'CivilBim.Revit.V04.dll'))
Set-Content -LiteralPath $manifest -Encoding utf8 -Value "<?xml version=`"1.0`" encoding=`"utf-8`"?><RevitAddIns><AddIn Type=`"Application`"><Name>CivilBIM</Name><Assembly>$dll</Assembly><AddInId>495CC06D-1D80-43F0-8B48-C57ED9A15436</AddInId><FullClassName>CivilBim.Revit.App</FullClassName><VendorId>CBIM</VendorId><VendorDescription>CivilBIM local test / version unchanged</VendorDescription></AddIn></RevitAddIns>"
Write-Host '현재 PC 로컬 시험본 적용. 버전 0.4.0 유지 / I: 배포물 변경 없음. Revit 재시작부터 적용됩니다.'
Write-Output $target
