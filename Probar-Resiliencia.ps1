#Requires -Version 7
# Probar-Resiliencia.ps1 · autoverificación de la práctica de la Clase 4
# Uso (PowerShell 7, desde la carpeta del kit, con «docker compose up -d --build» ya hecho en Infrastructure):
#     .\Probar-Resiliencia.ps1
# Tarda cerca de un minuto y medio: apaga y vuelve a encender Productos y Pagos, y espera al circuito.
# Cada prueba dice qué TODO revisar si no pasa. Para repetirla, espere un minuto (límite de pedidos por minuto).
param(
    [string]$Gateway = "http://localhost:5000",
    [string]$Pagos = "http://localhost:5004",
    [string]$Productos = "c4sol-product-grpc",
    [string]$ContenedorPagos = "c4sol-payment-grpc"
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

function Intentos([string]$servicio) {
    $r = Pedir GET "$Gateway/metricas/resiliencia"
    if (-not $r -or $r.StatusCode -ne 200) { return -1 }
    $s = @($r.Content | ConvertFrom-Json) | Where-Object { $_.servicio -eq $servicio }
    if ($s) { return [int]$s.intentos } else { return 0 }
}

function Stock() {
    $r = Pedir GET "$Gateway/api/v1/productos/1"
    if ($r -and $r.StatusCode -eq 200) { return [int]($r.Content | ConvertFrom-Json).stock }
    return -1
}

function CrearPedido([string]$token, [int]$cantidad = 1) {
    $r = Pedir POST "$Gateway/api/v1/pedidos" @{ lineas = @(@{ productoId = 1; cantidad = $cantidad }) } $token
    if ($r -and $r.StatusCode -eq 202) { return [int]($r.Content | ConvertFrom-Json).pedidoId }
    return 0
}

function EstadoPedido([int]$id, [string]$token) {
    $r = Pedir GET "$Gateway/api/v1/pedidos/$id" $null $token
    if ($r -and $r.StatusCode -eq 200) { return $r.Content | ConvertFrom-Json }
    return $null
}

function EsperarFin([int[]]$ids, [string]$token, [int]$segundos) {
    $fin = (Get-Date).AddSeconds($segundos)
    do {
        $estados = @($ids | ForEach-Object { EstadoPedido $_ $token })
        if (($estados | Where-Object { $_ -and $_.estado -ne "PENDIENTE" }).Count -eq $ids.Count) { return $estados }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $fin)
    return $estados
}

function Simular([double]$tasa) { $null = Pedir POST ("$Pagos/simulacion/pagos?tasaRechazo={0}" -f $tasa.ToString([cultureinfo]::InvariantCulture)) }

function Cobros() {
    $r = Pedir GET "$Pagos/pagos"
    if ($r -and $r.StatusCode -eq 200) { return @($r.Content | ConvertFrom-Json).Count }
    return -1
}

# ── Preparación ───────────────────────────────────────────────────────────
docker start $Productos $ContenedorPagos *> $null
Start-Sleep -Seconds 3
foreach ($u in @("$Gateway/health", "$Pagos/health")) {
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
Simular 0

try {
    # ── TODO 1 · reintento ────────────────────────────────────────────────
    Write-Host "Apagando Productos ($Productos)…" -ForegroundColor DarkGray
    docker stop $Productos *> $null
    $antes = Intentos "productos"
    $r = Pedir GET "$Gateway/api/v1/productos/1"
    $n = (Intentos "productos") - $antes
    $cod = if ($r) { $r.StatusCode } else { "—" }
    Prueba "1. Con Productos apagado, GET /productos/1 responde $cod después de 1 intento + 3 reintentos ($n intentos)" "TODO 1" ($r -and $r.StatusCode -in 503, 504 -and $n -eq 4)

    # ── TODO 2 · circuit breaker: se abre ─────────────────────────────────
    $abierto = $false; $ms = 0
    for ($i = 0; $i -lt 6 -and -not $abierto; $i++) {
        Start-Sleep -Milliseconds 1200
        $antes = Intentos "productos"
        $reloj = [Diagnostics.Stopwatch]::StartNew()
        $r = Pedir GET "$Gateway/api/v1/productos/1"
        $ms = $reloj.ElapsedMilliseconds
        $sinRed = ((Intentos "productos") - $antes) -eq 0
        $abierto = $r -and $r.StatusCode -eq 503 -and $r.Content -match '"circuito"\s*:\s*"abierto"' -and $sinRed
    }
    Prueba "2. El circuito se abre: 503 «circuito abierto» en $ms ms, sin llamar a Productos" "TODO 2" ($abierto -and $ms -lt 500)

    # ── TODO 2 · circuit breaker: se cierra ───────────────────────────────
    Write-Host "Encendiendo Productos y esperando a que el circuito pase a medio abierto (15 s)…" -ForegroundColor DarkGray
    docker start $Productos *> $null
    $cerro = $false
    $fin = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $fin -and -not $cerro) {
        Start-Sleep -Seconds 3
        $r = Pedir GET "$Gateway/api/v1/productos/1"
        $cerro = $r -and $r.StatusCode -eq 200
    }
    Prueba "3. Con Productos de vuelta, el circuito se cierra y GET /productos/1 responde 200" "TODO 2" ($abierto -and $cerro)

    # ── TODO 3 · compensación ─────────────────────────────────────────────
    Simular 1
    $s0 = Stock
    $id = CrearPedido $token 2
    $e = if ($id) { (EsperarFin @($id) $token 20)[0] } else { $null }
    $s1 = Stock
    $ok = $e -and $e.estado -eq "CANCELADO" -and $s0 -ge 0 -and $s1 -eq $s0 -and (($e.pasos -join " ") -match "Compensación")
    Prueba "4. Pago rechazado: el pedido $id queda CANCELADO y el stock vuelve a $s0 (hay $s1)" "TODO 3" $ok

    # ── TODO 4 · fallo transitorio ────────────────────────────────────────
    Simular 0
    Write-Host "Apagando Pagos ($ContenedorPagos) durante unos segundos…" -ForegroundColor DarkGray
    docker stop $ContenedorPagos *> $null
    $s0 = Stock
    $id = CrearPedido $token 1
    Start-Sleep -Seconds 6
    $durante = EstadoPedido $id $token
    docker start $ContenedorPagos *> $null
    $e = if ($id) { (EsperarFin @($id) $token 40)[0] } else { $null }
    $s1 = Stock
    $ok = $durante -and $durante.estado -eq "PENDIENTE" -and $e -and $e.estado -eq "CONFIRMADO" -and $s1 -eq ($s0 - 1)
    $txt = if ($durante) { $durante.estado } else { "—" }
    Prueba "5. Con Pagos apagado, el pedido $id espera ($txt) y termina CONFIRMADO al volver Pagos" "TODO 4" $ok

    # ── Saga con fallos al azar: el stock y los cobros cuadran ────────────
    Simular 0.5
    $s0 = Stock; $c0 = Cobros
    $ids = @(1..8 | ForEach-Object { CrearPedido $token 1 })
    $estados = if (($ids | Where-Object { $_ -gt 0 }).Count -eq 8) { EsperarFin $ids $token 40 } else { @() }
    $conf = @($estados | Where-Object { $_.estado -eq "CONFIRMADO" }).Count
    $canc = @($estados | Where-Object { $_.estado -eq "CANCELADO" }).Count
    $s1 = Stock; $c1 = Cobros
    $ok = ($conf + $canc) -eq 8 -and ($s0 - $s1) -eq $conf -and ($c1 - $c0) -eq $conf
    Prueba "6. Ocho pedidos con la mitad de los pagos rechazados: $conf confirmados, $canc cancelados; stock y cobros cuadran" "TODO 3" $ok
}
finally {
    Simular 0
    docker start $Productos $ContenedorPagos *> $null
}

Write-Host ""
$color = if ($script:pasa -eq $script:total) { "Green" } else { "Yellow" }
Write-Host ("Resultado: PASA {0}/{1}" -f $script:pasa, $script:total) -ForegroundColor $color
exit ([int]($script:pasa -ne $script:total))
