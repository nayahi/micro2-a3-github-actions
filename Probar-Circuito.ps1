#Requires -Version 7
# Probar-Circuito.ps1 · Clase 4 · pide un producto cada 1,5 s y muestra cuánto tardó y qué respondió.
# Sirve para ver el circuit breaker: apague Productos en otra terminal y mire cómo cambia.
# Uso (PowerShell 7, desde la carpeta del kit):
#     .\Probar-Circuito.ps1              → 20 solicitudes
#     .\Probar-Circuito.ps1 -Veces 40
# Pausa de 1,5 s: el gateway permite 10 lecturas cada 10 s por dirección IP (límite de la Clase 2).
param(
    [int]$Veces = 20,
    [string]$Gateway = "http://localhost:5000"
)

for ($i = 1; $i -le $Veces; $i++) {
    $reloj = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-WebRequest "$Gateway/api/v1/productos/1" -SkipHttpErrorCheck -TimeoutSec 30
    $ms = $reloj.ElapsedMilliseconds
    $qué = switch ($r.StatusCode) {
        200     { "stock " + ($r.Content | ConvertFrom-Json).stock }
        503     { if ($r.Content -match '"circuito"\s*:\s*"abierto"') { "CIRCUITO ABIERTO (no llamó a Productos)" } else { "Productos no responde (después de reintentar)" } }
        429     { "límite de lecturas: espere" }
        default { $r.Content }
    }
    $color = if ($r.StatusCode -eq 200) { "Green" } elseif ($ms -lt 300) { "Cyan" } else { "Yellow" }
    Write-Host ("{0:HH:mm:ss}  #{1,-3} HTTP {2}  {3,6} ms  {4}" -f (Get-Date), $i, $r.StatusCode, $ms, $qué) -ForegroundColor $color
    Start-Sleep -Milliseconds 1500
}
