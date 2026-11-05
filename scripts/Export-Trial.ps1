param([Parameter(Mandatory=$true)][string]$Destination,[Parameter(Mandatory=$true)][string]$AssemblyDirectory,[string]$TrialName='CivilBIM-0.4.0-trial-20261015-r2',[Parameter(Mandatory=$true)][string]$ExpectedDllHash)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$name=$TrialName
if($name -notmatch '^CivilBIM-[A-Za-z0-9.-]+$'){throw '시험본 이름은 CivilBIM- 접두사와 영문/숫자/점/하이픈만 허용합니다.'}
$package=Join-Path $repo ('artifacts\'+$name)
if(Test-Path -LiteralPath $package){throw '시험 패키지가 이미 존재합니다. 새 이름으로 보존하여 작업하세요.'}
$payload=Join-Path $package 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Get-ChildItem -LiteralPath $AssemblyDirectory -File | Where-Object {$_.Extension -in '.dll','.json','.pdb'} | Copy-Item -Destination $payload
if(Get-ChildItem -LiteralPath $payload -Filter 'RevitAPI*.dll'){throw 'Autodesk API 재배포 금지'}
if((Get-FileHash -LiteralPath (Join-Path $payload 'CivilBim.Revit.V04.dll')).Hash -ne $ExpectedDllHash){throw '지정한 빌드 해시와 불일치'}
Get-ChildItem -LiteralPath $payload -File | Sort-Object Name | ForEach-Object {"$((Get-FileHash -LiteralPath $_.FullName).Hash)  $($_.Name)"} | Set-Content -LiteralPath (Join-Path $payload 'SHA256SUMS.txt') -Encoding ascii
$installer=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Apply-Local.ps1'))
$guard=@'
$sumFile=Join-Path $source 'SHA256SUMS.txt'
foreach($line in (Get-Content -LiteralPath $sumFile)){
    if($line -notmatch '^([A-Fa-f0-9]{64})  ([^\\/]+)$'){throw 'Invalid payload checksum list'}
    $expected=$Matches[1];$file=Join-Path $source $Matches[2]
    if((Get-FileHash -LiteralPath $file).Hash -ne $expected){throw 'Damaged package: extract ZIP again'}
}
'@
$installer=$installer.Replace('$addins=',$guard+"`r`n"+'$addins=')
$installer=$installer.Replace('현재 PC 로컬 시험본 적용. 버전 0.4.0 유지 / I: 배포물 변경 없음. Revit 재시작부터 적용됩니다.','시험본 설치 완료. Revit 2027을 다시 실행하세요. 버전은 0.4.0입니다.')
[IO.File]::WriteAllText((Join-Path $package 'Install.ps1'),$installer,[Text.UTF8Encoding]::new($true))
Set-Content -LiteralPath (Join-Path $package 'Install.cmd') -Encoding ascii -Value @('@echo off','powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -AssemblyDirectory "%~dp0payload"','if errorlevel 1 (echo Installation failed. & pause & exit /b 1)','echo Trial installation complete. Restart Revit 2027.','pause')
Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $package -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $package
$guide=@'
CivilBIM 0.4.0 — 2026-10-15 시험 수정본 / Revit 2027 전용

Revit에서 작업을 저장하고 종료한 후 Install.cmd를 실행하세요.
기존 CivilBIM이 설치되어 있어도 삭제 없이 설치하면 됩니다.
새 시험본 폴더를 만들고 CivilBIM.addin 연결만 바꿉니다. 기존 DLL은 보존합니다.
이전 매니페스트는 새 설치 폴더의 previous-CivilBIM.addin.disabled에 백업합니다.
이전 버전으로 돌아가려면 기존 정식 배포본 Install.cmd를 다시 실행하세요.
관리자 실행은 필요하지 않습니다. 실제 Revit을 사용할 Windows 계정으로 설치하세요.

변경: 북방향 두 선 회전 / CIP 직경 입력 제거 / 전개도 검토·깊이 적용 / STRUT 검토·적용 분리.
추가 수정(r2): 북방향/전개도 상하부선은 Revit 참조선·참조평면·모델선 선택.
같은 좌표계의 PRJ 표기·파라미터 순서 차이를 허용. 실제 투영값이 다르면 파일명을 표시하고 중단.
추가 수정(r3): SHP 창에서 북방향 두 선의 각도·중심점을 설정한 뒤 생성 시 적용.
대규모 지형은 외곽/최저/최고점 보존 대표점으로 최대20,000점 자동 조절, 적용 격자와 개수를 로그에 표시.
자동 조절은 지형 근사입니다. 국부 상세 지형·정밀 굴착물량은 좁은 작업범위의 측량자료와 비교하세요.
사용법: docs\LOCAL-TEST.md. 기존 USER-GUIDE보다 이 수정본 안내를 우선 참고하세요.
테스트는 프로젝트 복사본으로 진행하세요.
검증: 빌드·코어 검사 통과. 실제 회전/도로/시추 정합 통과.
CIP 유형 환산 직경 검사 실패(원인 미확정), 전개도/STRUT 적용 검사 미완료.
사용자 직접 테스트용이며 완성판으로 확정하지 않았습니다. 제품 버전 0.4.0 유지.
PDF/SHP/DWG/RVT/RFA 등 실무 원본과 Autodesk API는 포함하지 않았습니다.
'@
[IO.File]::WriteAllText((Join-Path $package '먼저 읽어주세요.txt'),$guide,[Text.UTF8Encoding]::new($true))
$zip=$package+'.zip'
Compress-Archive -LiteralPath $package -DestinationPath $zip
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -LiteralPath $package -Destination $Destination -Recurse
Copy-Item -LiteralPath $zip -Destination $Destination
foreach($file in (Get-ChildItem -LiteralPath $package -Recurse -File)){
    $relative=$file.FullName.Substring($package.Length+1)
    $copy=Join-Path (Join-Path $Destination $name) $relative
    if((Get-FileHash -LiteralPath $copy).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw "Copy mismatch: $relative"}
}
if((Get-FileHash -LiteralPath (Join-Path $Destination ($name+'.zip'))).Hash -ne (Get-FileHash -LiteralPath $zip).Hash){throw 'ZIP copy mismatch'}
Set-Content -LiteralPath (Join-Path $Destination '00_최신시험본_설치하기.cmd') -Encoding ascii -Value @('@echo off',('call "%~dp0'+$name+'\Install.cmd"'))
[IO.File]::WriteAllText((Join-Path $Destination '00_최신시험본_안내.txt'),$guide,[Text.UTF8Encoding]::new($true))
Write-Output "Verified trial export: $Destination"
Get-FileHash -LiteralPath $zip
