param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$version='0.4.1'
$name='CivilBIM-'+$version
$package=Join-Path $repo ('artifacts\'+$name)
$zip=$package+'.zip'
if(!(Test-Path -LiteralPath $zip)){throw 'Build.ps1으로 설치 패키지를 먼저 만드세요.'}
$dirty=& git -C $repo status --porcelain
if($LASTEXITCODE -ne 0 -or $dirty){throw '추적 소스와 문서를 커밋하고 다시 배포하세요.'}
$commit=(& git -C $repo rev-parse HEAD).Trim()
$sourceZip=Join-Path $repo ('artifacts\'+$name+'-source.zip')
& git -C $repo archive --format=zip --prefix=CivilBIM-source/ --output=$sourceZip HEAD
if($LASTEXITCODE -ne 0){throw '소스 ZIP 생성 실패'}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -LiteralPath $zip,$sourceZip -Destination $Destination -Force
Copy-Item -LiteralPath $package -Destination $Destination -Recurse -Force
Set-Content -LiteralPath (Join-Path $Destination '01_설치하기.cmd') -Encoding ascii -Value @('@echo off',('if not exist "%~dp0'+$name+'\Install.cmd" (echo Copy the entire release folder to this PC first. & pause & exit /b 1)'),('call "%~dp0'+$name+'\Install.cmd"'))
$start=@"
CivilBIM $version / Revit 2027

간단 설치: 이 배포 폴더 전체를 다른 PC의 로컬 폴더로 복사한 뒤 01_설치하기.cmd를 실행하세요.
01_설치하기.cmd 옆의 $name 폴더도 반드시 함께 복사해야 합니다.

1. 다른 PC에서 $name.zip을 로컬 폴더에 전부 압축 해제하세요.
2. Revit 작업을 저장하고 닫은 다음 Install.cmd를 실행하세요.
3. Revit 2027을 다시 실행하여 CivilBIM 탭을 확인하세요.
   이미 풀어 둔 $name 폴더를 통째로 복사해 설치해도 됩니다.

설치/업데이트/로드 해제: $name\docs\INSTALL.md
단계별 사용법: $name\docs\USER-GUIDE.md
다른 PC 테스트 순서: $name\docs\TEST-CHECKLIST.md
변경 내용: $name\docs\CHANGELOG.md
굴착량 계산식/예시: $name\docs\QUANTITIES.md
검증 범위: $name\docs\VALIDATION.md
소스 코드: $name-source.zip (일반 설치에는 필요 없음)

PDF를 선택한 뒤 추출 표를 확인하고 '검토 완료 · 적용'을 누르면 연결됩니다.
굴착 바닥 EL은 최하단 표고입니다. EL15에서 깊이10m면 바닥 EL5m입니다.
시추 좌표는 조사 점의 위치이므로 실제 작업경계는 별도로 지정하세요.
CAD와 패밀리를 먼저 Revit에 넣고, 창의 새로고침 후 선택하세요.
공유좌표를 자동 설정하지 않습니다. 수동 이동 후 시추 위치와도 맞는지 확인하세요.
대지 회전각은 +반시계/-시계입니다. 생성 시 지형·도로·건물과 시추공에 공통 적용합니다.

실제 업무 원본 PDF/SHP/DWG/RVT/RFA는 이 배포에 포함하지 않았습니다.
새 PC에서 원본 자료를 복사하고 CAD 링크와 시추 파일 경로를 다시 지정하세요.
모델리스 창/계산/생성은 개발 PC에서 검증했으며 실제 고객 패밀리 호환성은 별도 확인이 필요합니다.
"@
[IO.File]::WriteAllText((Join-Path $Destination '먼저 읽어주세요.txt'),$start,[Text.UTF8Encoding]::new($true))
$info="CivilBIM $version`r`nSource commit: $commit`r`nBuilt target: Revit 2027 / net10.0-windows / x64`r`nExported: $([DateTimeOffset]::Now.ToString('o'))`r`nGitHub upload: none (user selected folder delivery)`r`n"
[IO.File]::WriteAllText((Join-Path $Destination 'RELEASE-INFO.txt'),$info,[Text.UTF8Encoding]::new($true))
$sums=foreach($file in @($zip,$sourceZip)){
    $copy=Join-Path $Destination (Split-Path -Leaf $file)
    $expected=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $expected){throw "복사 해시 불일치: $copy"}
    "$expected  $(Split-Path -Leaf $file)"
}
foreach($file in (Get-ChildItem -LiteralPath $package -File -Recurse)){
    $relative=$file.FullName.Substring($package.Length+1)
    $copy=Join-Path (Join-Path $Destination $name) $relative
    if((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash){throw "압축 해제 폴더 복사 해시 불일치: $relative"}
}
$sums | Set-Content -LiteralPath (Join-Path $Destination 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Export verified: $Destination"
Write-Output "Source commit: $commit"

