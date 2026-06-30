# Refresca el token de Claude usando el CLI OFICIAL (sin navegador, sin rate limit).
# Lo usa la tarea programada y el acceso directo del Escritorio.
$ErrorActionPreference = "SilentlyContinue"
$cli = (Get-ChildItem "$env:APPDATA\Claude\claude-code\*\claude.exe" |
        Sort-Object FullName -Descending | Select-Object -First 1).FullName
if ($cli) { & $cli auth status | Out-Null }
