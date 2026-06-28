# Claude Widget 🐾

Un **widget de escritorio flotante para Windows** que te muestra tu consumo de
Claude en tiempo real, te avisa cuando una conversación espera tu permiso (y te
deja **aprobar/denegar** desde el propio widget o el móvil) y lleva una **mascota
animada de Claude** que reacciona a lo que pasa.

Hecho en **C# / WPF (.NET 8)**… construido conversando con Claude Code, sin ser
programador. Puedes hacer lo mismo: dale la URL de este repo a tu Claude y que te
lo instale (ver abajo).

> ⚠️ **Solo Windows 10/11.**

---

## ✨ Qué hace

- 📊 **Consumo real del plan**: % de la **sesión (5h)** y del **límite semanal**, con
  las horas exactas de reinicio (igual que el panel `/usage` de Claude).
- 🧮 **Tokens y coste** de hoy / semana / mes (vía [ccusage](https://github.com/ryoppippi/ccusage)).
- 🔔 **Aviso de permiso**: cuando una conversación espera tu validación, parpadea,
  suena y muestra **✓ Aprobar / ✕ Denegar**. Respondas donde respondas (móvil,
  widget o terminal), el aviso se quita solo.
- 🟢 **Interruptor "Auto"**: activa/desactiva la aceptación automática.
- 🐾 **Mascota animada** que reacciona: se concentra si subes de uso, se sorprende
  cuando esperas un permiso, baila al reiniciarse un límite y se duerme si la dejas
  sola. (Click derecho = ver todas las animaciones.)
- 🪟 Ventana flotante arrastrable, recuerda posición, **arranca con Windows** y se
  **muestra/oculta** con la app de Claude.

---

## 🤖 Instálalo con tu propio Claude (recomendado)

1. Abre **Claude Code** (app de escritorio o terminal) en una carpeta vacía.
2. Pégale esto:

   > Lee el README de https://github.com/DavidVaqueroDiaz/claude-widget y ayúdame
   > a instalar el widget paso a paso en mi Windows. Ve diciéndome qué tengo que
   > hacer yo a mano (los pasos manuales) y haz tú el resto.

3. Sigue lo que te vaya pidiendo. Hay **3 pasos manuales** que solo puedes hacer tú
   (marcados abajo con 🙋).

---

## 🧰 Requisitos

- **Windows 10/11**
- **.NET 8 SDK** — https://dotnet.microsoft.com/download/dotnet/8.0
- **Node.js 18+** — https://nodejs.org
- **Claude Code** con suscripción (Pro/Max) y sesión iniciada.

---

## 📦 Instalación manual (paso a paso)

```powershell
# 1) Clonar
git clone https://github.com/DavidVaqueroDiaz/claude-widget.git
cd claude-widget

# 2) Compilar la versión estable
dotnet publish -c Release -o app

# 3) Consumo de tokens/coste
npm install -g ccusage
```

### 🙋 Paso manual 1 — Iniciar sesión para el % exacto del plan
El % exacto (igual que `/usage`) usa tu token de `claude login`:

```powershell
claude login
```
Si `claude` no está en el PATH, usa la ruta completa (cambia la versión por la tuya):
```powershell
& "$env:APPDATA\Claude\claude-code\<VERSION>\claude.exe" login
```
Esto crea `~/.claude/.credentials.json`, que el widget lee (no descifra nada).

### 🙋 Paso manual 2 — Aprobar/denegar permisos (opcional pero recomendado)
Las funciones de permiso usan [claude-remote-approver](https://www.npmjs.com/package/claude-remote-approver):

```powershell
npm install -g claude-remote-approver
claude-remote-approver setup     # muestra un QR
```
Instala la **app ntfy** en tu móvil ([iOS](https://apps.apple.com/app/ntfy/id1625396347) ·
[Android](https://play.google.com/store/apps/details?id=io.heckel.ntfy)) y **escanea el QR**.
(Si no instalas esto, el consumo y la mascota funcionan igual; solo no tendrás aprobar/denegar.)

### 🙋 Paso manual 3 — Arrancar
```powershell
.\app\ClaudeWidget.exe
```
Se registra solo para **arrancar con Windows** y aparece cuando abres Claude.

---

## ⚙️ Configuración (botón ⚙ del widget)
- **% exacto del plan (on/off)** — desactívalo si no quieres usar el endpoint no oficial.
- Qué datos mostrar, always-on-top, parpadeo/sonido del aviso.
- Arrancar con Windows · mostrar solo con Claude abierto.
- Reinicio del límite semanal · plan · intervalo de refresco.

Los ajustes se guardan en `%APPDATA%\ClaudeWidget\settings.json` (no se sube al repo).
Si tienes el approver **clonado en local** en vez de global, pon su ruta en
`ApproverCliPath` dentro de ese settings.json.

---

## ⚠️ Avisos importantes (léelos)

- El **% exacto del plan** consulta un endpoint **no oficial** de Anthropic con tu
  token de login (el mismo método que el proyecto público
  [Clawdmeter](https://github.com/HermannBjorgvin/Clawdmeter)). **Puede dejar de
  funcionar** en cualquier actualización de Claude, y es una **zona gris** respecto a
  las condiciones de Anthropic. Úsalo bajo tu responsabilidad. Si no quieres, ponlo
  en OFF en Ajustes y tendrás solo tokens/coste de ccusage (100% limpio).
- Si el widget dice **"token caducado → claude login"**, vuelve a ejecutar `claude login`.

---

## 🙏 Créditos
- 🐾 Mascota animada **Clawd** y sus fotogramas: del proyecto
  [Clawdmeter](https://github.com/HermannBjorgvin/Clawdmeter) (assets de marca de
  Anthropic; ver [LICENSE](LICENSE)).
- 🔔 Permisos remotos: [claude-remote-approver](https://www.npmjs.com/package/claude-remote-approver).
- 📊 Consumo: [ccusage](https://github.com/ryoppippi/ccusage).

## 📄 Licencia
Código bajo [MIT](LICENSE). Los assets de la mascota son material de marca de
Anthropic (zona gris) — no para redistribución comercial.
