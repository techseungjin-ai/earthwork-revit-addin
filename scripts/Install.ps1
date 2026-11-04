param([string]$AssemblyDirectory,[string]$RevitDirectory)
$ErrorActionPreference='Stop'
if(!$AssemblyDirectory){$AssemblyDirectory=Join-Path (Split-Path -Parent $PSScriptRoot) 'src\CivilBim.Revit\bin\Release\net10.0-windows'}
$AssemblyDirectory=(Resolve-Path -LiteralPath $AssemblyDirectory).Path
if(!(Test-Path -LiteralPath (Join-Path $AssemblyDirectory 'CivilBim.Revit.V04.dll'))){throw '0.4 빌드 DLL이 없습니다.'}
if(!$RevitDirectory){
    $candidates=@('C:\Program Files\Autodesk\Revit 2027')
    foreach($key in @('HKLM:\SOFTWARE\Autodesk\Revit\2027','HKLM:\SOFTWARE\Autodesk\Revit\2027\REVIT-05','HKLM:\SOFTWARE\Autodesk\Revit\Autodesk Revit 2027')){
        $entry=Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        foreach($value in @($entry.InstallLocation,$entry.InstallationLocation,$entry.InstallRoot)){if($value){$candidates+=$value;$candidates+=(Join-Path $value 'Revit 2027')}}
    }
    $RevitDirectory=$candidates | Where-Object {Test-Path -LiteralPath (Join-Path $_ 'Revit.exe')} | Select-Object -First 1
}
if(!$RevitDirectory -or !(Test-Path -LiteralPath (Join-Path $RevitDirectory 'Revit.exe'))){throw 'Revit 2027 설치를 찾지 못했습니다. 사용자 지정 경로는 Install.ps1 -RevitDirectory 옵션으로 지정하세요. 이 패키지는 Revit 2027 전용입니다.'}
$checks=Join-Path $AssemblyDirectory 'SHA256SUMS.txt'
if(Test-Path -LiteralPath $checks){
    foreach($line in (Get-Content -LiteralPath $checks)){
        if($line -notmatch '^([A-Fa-f0-9]{64})  ([^\\/]+)$'){throw '패키지 해시 목록 형식이 잘못되었습니다.'}
        $expected=$Matches[1];$inputFile=Join-Path $AssemblyDirectory $Matches[2]
        if(!(Test-Path -LiteralPath $inputFile) -or (Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash -ne $expected){throw "패키지가 누락되었거나 손상되었습니다: $inputFile. ZIP을 다시 내려받아 압축 해제하세요."}
    }
}
$addins=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027'
$target=Join-Path $addins 'CivilBIM\0.4.0'
New-Item -ItemType Directory -Path $target -Force | Out-Null
foreach($file in (Get-ChildItem -LiteralPath $AssemblyDirectory -File | Where-Object {$_.Extension -in '.dll','.json','.pdb'})){
    $destination=Join-Path $target $file.Name
    if((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash){continue}
    try{Copy-Item -LiteralPath $file.FullName -Destination $destination -Force}catch{throw '0.4 애드인 파일이 사용 중입니다. 저장 후 해당 Revit을 닫고 다시 설치하세요.'}
    if((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw "설치 파일 검증 실패: $destination"}
}
$assembly=[System.Security.SecurityElement]::Escape((Join-Path $target 'CivilBim.Revit.V04.dll'))
$manifest=@"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns><AddIn Type="Application"><Name>CivilBIM</Name><Assembly>$assembly</Assembly><AddInId>495CC06D-1D80-43F0-8B48-C57ED9A15436</AddInId><FullClassName>CivilBim.Revit.App</FullClassName><VendorId>CBIM</VendorId><VendorDescription>CivilBIM Revit civil modeling</VendorDescription></AddIn></RevitAddIns>
"@
Set-Content -LiteralPath (Join-Path $addins 'CivilBIM.addin') -Value $manifest -Encoding utf8
Write-Output "Installed: $target"
Write-Output '다음 Revit 실행부터 적용됩니다. 현재 열린 프로젝트는 그대로 유지됩니다.'

