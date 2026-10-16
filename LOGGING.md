# Logs persistentes

La consola de Microsoft.Extensions.Logging se mantiene. Serilog añade un proveedor
de archivo con timestamp, nivel, categoría, mensaje, propiedades estructuradas y
excepción con stack trace. No se registran mensajes RAW completos en Information.
Los resúmenes de eventos, duplicados, ACK y reconexiones conservan sus niveles.
Sólo las entradas `TX frame` de los siete emisores ACK pasan a Debug; no cambia
la construcción ni el envío de ningún ACK.

Por defecto se escribe `logs/neximbridge-YYYYMMDD.log` junto a la aplicación.
Con el build Debug actual: `bin/Debug/net8.0-windows/logs/`.
Las rutas relativas se resuelven con `AppContext.BaseDirectory`, nunca con el
directorio de trabajo del proceso. También se admiten rutas absolutas.

Configuración en `appsettings.json` junto al ejecutable:

```json
"NeximLogging": {
  "FilePath": "logs/neximbridge-.log",
  "RetainedFileCountLimit": 30,
  "FileSizeLimitBytes": 20971520
}
```

Rotación diaria y por aproximadamente 20 MiB, con sufijos para archivos del mismo
día. Se retienen 30 **archivos**, no necesariamente 30 días. `shared=false`: una
sola instancia escribe el archivo. Reiniciar para cambiar ruta o rotación.
El sink usa escritura sin buffering y se dispone después de detener el Host.

Los niveles se controlan mediante `Logging:LogLevel`: Default=Information,
Microsoft/System=Warning, conservando Microsoft.Hosting.Lifetime=Information.
Para activar temporalmente Debug sólo en NeximBridge, añadir:

```json
"NeximBridge": "Debug"
```

dentro de `Logging:LogLevel`. También se puede usar la variable
`Logging__LogLevel__NeximBridge=Debug`. Retirar el override al terminar: Debug
incluye frames completos y aumenta el volumen. No añadir credenciales ni cadenas
de conexión a los mensajes de diagnóstico.

La configuración de logging se carga antes de construir el Host, incluyendo
`appsettings.{DOTNET_ENVIRONMENT}.json`, variables de entorno y argumentos.
Un error de arranque del Host se registra con excepción. Si el archivo falla,
la consola sigue disponible; los fallos internos del sink generan un aviso genérico
en stderr limitado a uno por minuto, sin volcar payloads internos. No es requisito
que el archivo esté disponible para procesar eventos.

Paquetes directos añadidos: Serilog.Extensions.Logging 10.0.0 y
Serilog.Sinks.File 7.0.0, seleccionados por NuGet sin fijar una versión antigua.
La rotación utiliza el [sink oficial de archivos](https://www.nuget.org/packages/Serilog.Sinks.File/).
El proveedor se registra directamente para que respete los filtros existentes de
Microsoft.Extensions.Logging, sin el override de nivel que añade `AddSerilog()`.

Pruebas sin Nexim real ni SQL:

```powershell
dotnet build
dotnet build .\NeximSimulator\NeximSimulator.csproj
dotnet run --project .\tests\NeximBridge.LoggingTests
dotnet run --project .\tests\NeximBridge.ProtocolTests
dotnet run --project .\tests\NeximBridge.PartsUsageTests
```

Las pruebas de logging usan un Host sin servicios TCP/SQL y directorios temporales.
Comprueban rutas, arranque, Information, excepciones, filtrado Debug, flush, cierre
de archivos, rotación/retención y fallback ante una ruta no escribible.
