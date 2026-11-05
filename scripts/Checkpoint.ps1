param([Parameter(Mandatory=$true)][string]$Message,[switch]$SyncCommon)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$handover=Join-Path $repo 'HANDOVER.md'
$stamp=[TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow,'Korea Standard Time').ToString('yyyy-MM-dd HH:mm:ss')
$snapshot=Get-Content -LiteralPath $handover -Raw
$snapshot=[regex]::Replace($snapshot,'(?m)^\d{4}-\d{2}-\d{2}.*Codex。?\.?$',"$stamp (Asia/Seoul), Codex.")
Set-Content -LiteralPath $handover -Value $snapshot -Encoding utf8
Add-Content -LiteralPath $handover -Encoding utf8 -Value "`n- 체크포인트 $stamp (Asia/Seoul): $Message"
& git -C $repo add -u
$scoped=@('AGENTS.md','HANDOVER.md','README.md','.gitignore','Directory.Build.props','NuGet.Config','src','tests','scripts','docs','samples') | Where-Object {Test-Path -LiteralPath (Join-Path $repo $_)}
& git -C $repo add -- @scoped
if($LASTEXITCODE -ne 0){throw 'git add 실패'}
& git -C $repo commit -m $Message
if($LASTEXITCODE -ne 0){throw 'git commit 실패'}
if($SyncCommon){
    $common='C:\Users\최승진\Desktop\안티그래비티\CODEX-ANTIGRAVITY'
    Copy-Item -LiteralPath $handover -Destination (Join-Path $common 'HANDOVER.md') -Force
    & git -C $common add -- HANDOVER.md AGENTS.md
    & git -C $common diff --cached --quiet
    if($LASTEXITCODE -ne 0){& git -C $common commit -m $Message; if($LASTEXITCODE -ne 0){throw '공통 인계 커밋 실패'}}
}
