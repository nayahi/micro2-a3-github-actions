#Requires -Version 7
# Probar-Mensajeria.ps1 · autoverificación de la práctica de la Clase 3
# Uso (PowerShell 7, desde la carpeta del kit, con «docker compose up -d --build» ya hecho en Infrastructure):
#     .\Probar-Mensajeria.ps1
# Tarda cerca de un minuto y medio: espera a que los mensajes viajen y a que se cumplan los reintentos.
# Cada prueba dice qué TODO revisar si no pasa.
param(
    [string]$Gateway = "http://localhost:5000",
    [string]$Notificaciones = "http://localhost:5003",
    [string]$LocalStack = "c4sol-localstack"
)

$script:pasa = 0
$script:total = 0

function Pedir([string]$metodo, [string]$url, $cuerpo = $null, [string]$token = "") {
    $h = @{}
    if ($token) { $h["Authorization"] = "Bearer $token" }
    $p = @{ Method = $metodo; Uri = $url; Headers = $h; SkipHttpErrorCheck = $true; TimeoutSec = 30 }
    if ($null -ne $cuerpo) { $p.Body = ($cuerpo | ConvertTo-Json -Depth 5); $p.ContentType = "application/json" }
    try { return Invoke-WebRequest @p } catch { return $null }
}

function Prueba([string]$nombre, [string]$todo, [bool]$ok) {
    $script:total++
    if ($ok) { $script:pasa++; Write-Host ("PASA   {0}" -f $nombre) -ForegroundColor Green }
    else     { Write-Host ("FALLA  {0}   (revise el {1})" -f $nombre, $todo) -ForegroundColor Yellow }
}

function Notificaciones([int]$pedido) {
    $r = Pedir GET "$Notificaciones/notificaciones"
    if (-not $r -or $r.StatusCode -ne 200) { return @() }
    return @(($r.Content | ConvertFrom-Json) | Where-Object { $_.pedidoId -eq $pedido })
}

function Colas() {
    $r = Pedir GET "$Notificaciones/colas"
    if ($r -and $r.StatusCode -eq 200) { return $r.Content | ConvertFrom-Json -AsHashtable }
    return @{}
}

function Esperar([int]$segundos, [scriptblock]$condicion) {
    $fin = (Get-Date).AddSeconds($segundos)
    while ((Get-Date) -lt $fin) {
        if (& $condicion) { return $true }
        Start-Sleep -Seconds 2
    }
    return [bool](& $condicion)
}

function CrearPedido([string]$token) {
    $r = Pedir POST "$Gateway/api/v1/pedidos" @{ lineas = @(@{ productoId = 1; cantidad = 1 }) } $token
    if ($r -and $r.StatusCode -eq 202) { return ($r.Content | ConvertFrom-Json).pedidoId }
    return 0
}

# ── Preparación ───────────────────────────────────────────────────────────
foreach ($u in @("$Gateway/health", "$Notificaciones/health")) {
    $s = Pedir GET $u
    if ($null -eq $s -or $s.StatusCode -ne 200) {
        Write-Host "No responde $u. Revise «docker compose ps» en la carpeta Infrastructure." -ForegroundColor Red
        exit 1
    }
}
$login = Pedir POST "$Gateway/api/auth/login" @{ email = "user@example.com"; clave = "Cliente2026!" }
if (-not $login -or $login.StatusCode -ne 200) {
    Write-Host "No se pudo iniciar sesión con user@example.com (POST /api/auth/login)." -ForegroundColor Red
    exit 1
}
$token = ($login.Content | ConvertFrom-Json).token
$null = Pedir POST "$Notificaciones/simulacion/correo-caido?activo=false"

# ── TODO 1 · publicar OrderCreated ────────────────────────────────────────
$pedido = CrearPedido $token
Prueba "1. POST /api/v1/pedidos responde 202 con el número de pedido" "TODO 1" ($pedido -gt 0)

# ── TODO 2 · recibir de la cola ───────────────────────────────────────────
$llego = $pedido -gt 0 -and (Esperar 25 { (Notificaciones $pedido).Count -ge 1 })
Prueba "2. La notificación del pedido $(if ($pedido) { $pedido } else { '(no se creó)' }) llega a NotificationService (≤ 25 s)" "TODO 2" $llego

# ── TODO 3 · borrar después de notificar ──────────────────────────────────
Start-Sleep -Seconds 12      # más que el visibility timeout (5 s): si no se borró, ya volvió
$logs = docker logs --since 40s "c4sol-notification" 2>&1 | Out-String
$c = Colas
$ok = $llego -and (Notificaciones $pedido).Count -eq 1 -and
      ($logs -notmatch "Duplicado: .*\(pedido $pedido\)") -and
      ($c["pedidos-notificar (visibles)"] + $c["pedidos-notificar (en proceso)"]) -eq 0
Prueba "3. El mensaje se borra después de notificar: una notificación y la cola vacía" "TODO 3" $ok

# ── TODO 4 · idempotencia ─────────────────────────────────────────────────
$dup = Get-Random -Minimum 90000 -Maximum 99999
docker exec $LocalStack bash /scripts/publicar-duplicado.sh $dup | Out-Null
Start-Sleep -Seconds 12
$n = (Notificaciones $dup).Count
Prueba "4. El mismo evento publicado dos veces produce UNA notificación ($n)" "TODO 4" ($n -eq 1)

# ── TODO 5 · DLQ ──────────────────────────────────────────────────────────
$null = Pedir POST "$Notificaciones/simulacion/correo-caido?activo=true"
$antes = (Colas)["pedidos-notificar-dlq (visibles)"]
$fallido = CrearPedido $token
$enDlq = $fallido -gt 0 -and (Esperar 45 { (Colas)["pedidos-notificar-dlq (visibles)"] -gt $antes })
$null = Pedir POST "$Notificaciones/simulacion/correo-caido?activo=false"
Prueba "5. Con el correo caído, el pedido $(if ($fallido) { $fallido } else { '(no se creó)' }) termina en pedidos-notificar-dlq (≤ 45 s)" "TODO 5" $enDlq

# ── Reprocesar la DLQ (ya viene en el kit; necesita los TODO 2, 3 y 5) ───
$r = Pedir POST "$Notificaciones/dlq/reprocesar"
$ok = $enDlq -and $r -and $r.StatusCode -eq 200 -and (Esperar 25 { (Notificaciones $fallido).Count -ge 1 })
Prueba "6. Al reprocesar la DLQ, el pedido fallido se notifica" "TODO 5" $ok

Write-Host ""
$color = if ($script:pasa -eq $script:total) { "Green" } else { "Yellow" }
Write-Host ("Resultado: PASA {0}/{1}" -f $script:pasa, $script:total) -ForegroundColor $color
exit ([int]($script:pasa -ne $script:total))
