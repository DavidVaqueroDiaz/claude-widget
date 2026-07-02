# Refresca el token de Claude usando el CLI OFICIAL (sin navegador, sin rate limit).
# Hace una micro-consulta (-p "ok") que fuerza al CLI a renovar y GUARDAR el token.
# Lo usa la tarea programada "ClaudeWidget-RefrescarToken" y el acceso directo.
$ErrorActionPreference = "SilentlyContinue"
Set-Location $env:USERPROFILE
$cli = (Get-ChildItem "$env:APPDATA\Claude\claude-code\*\claude.exe" |
        Sort-Object FullName -Descending | Select-Object -First 1).FullName
if ($cli) { & $cli -p "responde solo con: ok" --output-format text | Out-Null }
