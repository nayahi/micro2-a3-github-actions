# ECommerceGRPC · repositorio base del curso (Asincrónico 3)

Microservicios Resilientes con .NET Core y AWS, Nivel 2 (CPIC, 2026). Es el sistema de la Clase 4 ya resuelto: gateway REST con JWT, servicios gRPC, mensajería con SNS y SQS (LocalStack), Polly y la saga del pedido.

## Qué hace el workflow

`.github/workflows/build.yml` corre en cada `push`: restaura, compila la solución y ejecuta `dotnet test` sobre `Pruebas/`.

| Prueba | Qué comprueba | Necesita el secreto |
|---|---|---|
| `Un_servicio_caido_recibe_un_intento_y_tres_reintentos` | El reintento de Polly del gateway | No |
| `Despues_de_varios_fallos_el_circuito_se_abre_y_falla_sin_llamar` | El circuit breaker del gateway | No |
| `El_gateway_firma_y_valida_un_token_con_el_secreto_JWT_CLAVE` | Que el token se firma con la clave del secreto | **Sí: `JWT_CLAVE`, 32 caracteres o más** |

Sin el secreto, el workflow queda en rojo con el mensaje `Falta el secreto JWT_CLAVE`. Los pasos para su fork están en la guía del Asincrónico 3, en Classroom.

## Probar en su máquina

```powershell
$env:JWT_CLAVE = "una-clave-local-de-32-caracteres-o-mas"
dotnet test Pruebas/ECommerceGRPC.Pruebas.csproj
```

Para levantar todo el sistema: en `Infrastructure`, `docker compose up -d --build`. Las claves de `appsettings.json` y de `docker-compose.yml` son de desarrollo local: nunca las use fuera de su máquina.

---

Elaborado por Jairo Alberto Zúñiga Gómez © 2026
