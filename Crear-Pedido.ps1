#Requires -Version 7
# Crear-Pedido.ps1 · inicia sesión en el gateway, crea un pedido (POST /api/v1/pedidos) y sigue su saga.
# Uso (PowerShell 7, desde la carpeta del kit):
#     .\Crear-Pedido.ps1                 → 1 unidad del producto 1, como user@example.com
#     .\Crear-Pedido.ps1 -Producto 2 -Cantidad 3
#     .\Crear-Pedido.ps1 -SinEsperar     → solo crea el pedido (como en la Clase 3)
# Clase 4: después del 202 consulta GET /api/v1/pedidos/{id} cada segundo, hasta que deje de estar PENDIENTE
# (máximo 40 s), y muestra el estado y los pasos de la saga.
param(
    [int]$Producto = 1,
    [int]$Cantidad = 1,
    [string]$Email = "user@example.com",
    [string]$Clave = "Cliente2026!",
    [string]$Gateway = "http://localhost:5000",
    [switch]$SinEsperar
)

$login = Invoke-RestMethod -Method Post "$Gateway/api/auth/login" -ContentType "application/json" `
    -Body (@{ email = $Email; clave = $Clave } | ConvertTo-Json)
$h = @{ Authorization = "Bearer $($login.token)" }
$cuerpo = @{ lineas = @(@{ productoId = $Producto; cantidad = $Cantidad }) } | ConvertTo-Json -Depth 3
$r = Invoke-WebRequest -Method Post "$Gateway/api/v1/pedidos" -ContentType "application/json" `
    -Headers $h -Body $cuerpo -SkipHttpErrorCheck
Write-Host ("HTTP {0}" -f $r.StatusCode)
$r.Content
if ($SinEsperar -or $r.StatusCode -ne 202) { return }

$id = ($r.Content | ConvertFrom-Json).pedidoId
$fin = (Get-Date).AddSeconds(40)
do {
    Start-Sleep -Seconds 1
    $e = Invoke-RestMethod "$Gateway/api/v1/pedidos/$id" -Headers $h
} while ($e.estado -eq "PENDIENTE" -and (Get-Date) -lt $fin)

Write-Host ""
Write-Host ("Pedido {0}: {1}" -f $id, $e.estado) -ForegroundColor $(if ($e.estado -eq "CONFIRMADO") { "Green" } elseif ($e.estado -eq "CANCELADO") { "Yellow" } else { "Cyan" })
$e.pasos | ForEach-Object { Write-Host "  $_" }
