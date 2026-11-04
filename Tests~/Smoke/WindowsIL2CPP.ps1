param([Parameter(Mandatory)][string]$Unity,[Parameter(Mandatory)][string]$ValidationProject,[ValidateSet('IL2CPP','Mono')][string]$Backend='IL2CPP')
$ErrorActionPreference='Stop'
$project=(Resolve-Path -LiteralPath $ValidationProject).Path
$out=Join-Path $project 'GASValidation~'
New-Item -Path $out -ItemType Directory -Force | Out-Null
$build=Start-Process -FilePath $Unity -ArgumentList @('-batchmode','-quit','-projectPath',('"'+$project+'"'),'-executeMethod',("ZFramework.GAS.Samples.BasicCombat.Editor.BasicCombatSmokeBuild.Windows"+$Backend),'-logFile',('"'+(Join-Path $out "$Backend-build.log")+'"')) -WindowStyle Hidden -Wait -PassThru
if($build.ExitCode -ne 0){throw "Build failed ($($build.ExitCode)): $out/$Backend-build.log"}
$log=Join-Path $out ($Backend+'-player-'+[Guid]::NewGuid().ToString('N')+'.log')
$player=Start-Process -FilePath (Join-Path $out "$Backend/GASSmoke.exe") -ArgumentList @('-batchmode','-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
if(-not $player.WaitForExit(180000)){$player.Kill();throw "Player timed out: $log"}
if($player.ExitCode -ne 0 -or -not (Select-String -Path $log -Pattern "GAS_${Backend}_SMOKE_PASSED" -Quiet)){throw "Smoke failed: $log"}
Write-Output "BasicCombat $Backend five-cast smoke passed: $log"
